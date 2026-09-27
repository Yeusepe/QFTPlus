"""Optional DirectML inference; CPU training and fallback work on every PC."""
import hashlib
import copy
import io
import math
import os
import statistics
import sys
import threading
import tempfile
import time
from pathlib import Path
import numpy as np
import torch
from prepare_training import resize_cameras




_cuda_lock = threading.RLock()


class AreaResizeModel(torch.nn.Module):
    """Exact uint8 area resize and normalization in the inference graph."""
    raw_input = True

    def __init__(self, model, size, count):
        super().__init__()
        self.model, self.size, self.count = model, size, count
        self.cuda_resize = None
        self.tiles = math.gcd(400, size)
        self.source = 400 // self.tiles
        self.target = size // self.tiles
        x = torch.arange(self.source)[None, :]
        y = torch.arange(self.target)[:, None]

        weights = (torch.minimum((y+1)*self.source, (x+1)*self.target)
                   - torch.maximum(y*self.source, x*self.target)).clamp(min=0)
        self.register_buffer("weights", torch.einsum("ai,bj->ijab", weights, weights)
                             .reshape(self.source**2, self.target**2).float())

        self.register_buffer("levels", torch.from_numpy(np.arange(256, dtype=np.float32)/255))

    def resize(self, raw):
        if self.cuda_resize is not None:
            return self.cuda_resize(raw, self.levels)
        n, g, p, q = self.count, self.tiles, self.source, self.target
        patches = (raw.reshape(400, n, 400).permute(1, 0, 2).reshape(n, g, p, g, p)
                   .permute(0, 1, 3, 2, 4).reshape(n*g*g, p*p).float())

        pixels = (torch.mm(patches, self.weights)/(p*p)).round().long()
        values = self.levels[pixels]
        return values.reshape(n, g, g, q, q).permute(0, 1, 3, 2, 4).reshape(1, n, self.size, self.size)

    def forward(self, raw):
        return self.model(self.resize(raw))

    def enable_cuda_kernel(self, raw):
        try:
            from cuda_area_resize import CudaAreaResize
            kernel = CudaAreaResize(self.size, self.count, raw.device)
            expected = self.resize(raw)
            torch.testing.assert_close(kernel(raw, self.levels), expected, rtol=0, atol=0)
            self.cuda_resize = kernel
            print("INFERENCE_PREPROCESS CUDA area kernel", flush=True)
        except Exception as error:
            print("INFERENCE_OPTIMIZATION_SKIPPED CUDA area kernel: " + str(error), flush=True)


def prepare_inputs(model, strip, size, count):
    if getattr(model, "raw_input", False):
        if strip.dtype != np.uint8 or strip.ndim != 2 or strip.shape[0] != 400 or strip.shape[1] < count*400:
            raise ValueError("Expected complete uint8 camera panels")

        return torch.from_numpy(strip[:, :count*400].copy())
    return torch.from_numpy(resize_cameras(strip, size, count)[None].astype(np.float32)/255)


class CudaModel:
    """Fixed-shape FP32 inference with one graph launch and one CPU copy."""
    def __init__(self, model, shape):
        self.model = model
        self.raw_input = getattr(model, "raw_input", False)
        self.graph = None
        with _cuda_lock, torch.inference_mode():
            self.inputs = torch.zeros(shape, device=next(model.parameters()).device,
                                      dtype=torch.uint8 if self.raw_input else torch.float32)
            if self.raw_input:
                model.enable_cuda_kernel(self.inputs)
            stream = torch.cuda.Stream(device=self.inputs.device)
            stream.wait_stream(torch.cuda.current_stream(self.inputs.device))
            with torch.cuda.stream(stream):
                for _ in range(3):
                    model(self.inputs)
            torch.cuda.current_stream(self.inputs.device).wait_stream(stream)
            try:
                graph = torch.cuda.CUDAGraph()
                with torch.cuda.graph(graph):
                    self.output = self._forward()
                self.graph = graph
                print("INFERENCE_BACKEND CUDA graph", flush=True)
            except RuntimeError as error:
                print("INFERENCE_FALLBACK CUDA eager: " + str(error), flush=True)

    def _forward(self):
        output = self.model(self.inputs)
        self.widths = [part.shape[1] for part in output] if isinstance(output, tuple) else None
        return torch.cat(output, dim=1) if self.widths is not None else output

    def __call__(self, inputs):
        if inputs.shape != self.inputs.shape or inputs.dtype != self.inputs.dtype:
            raise ValueError("CUDA inference input shape or dtype changed")
        with _cuda_lock, torch.inference_mode():
            self.inputs.copy_(inputs)
            if self.graph is not None:
                self.graph.replay()
                output = self.output
            else:
                output = self._forward()

            values = output.cpu()
            return torch.split(values, self.widths, dim=1) if self.widths is not None else values


class TonguePair(torch.nn.Module):
    """Keep each model's weights while sharing upload, resize and CPU return."""
    def __init__(self, gate, direction):
        super().__init__()
        self.gate, self.direction = gate, direction

    def forward(self, inputs):
        return torch.cat((self.gate(inputs), self.direction(inputs)), dim=0)


_trt_logger = None


class TensorRTTongue(torch.nn.Module):
    """Optional fixed-shape engine; pointers remain alive through graph replay."""
    def __init__(self, source, size, precision):
        super().__init__()
        import tensorrt as trt
        global _trt_logger
        if _trt_logger is None:
            _trt_logger = trt.Logger(trt.Logger.WARNING)
        self.dtype = torch.float32
        self.precision = precision
        device = next(source.parameters()).device
        self.anchor = torch.nn.Parameter(torch.empty(0, device=device), requires_grad=False)

        exported = io.BytesIO()
        torch.onnx.export(copy.deepcopy(source).to(device="cpu", dtype=self.dtype).eval(),
                          torch.zeros((1, 2, size, size), dtype=self.dtype), exported,
                          input_names=["cameras"], output_names=["values"],
                          opset_version=17, dynamo=False)
        onnx_bytes = exported.getvalue()
        identity = repr((trt.__version__, torch.version.cuda, torch.cuda.get_device_name(device),
                         torch.cuda.get_device_capability(device), precision)).encode()
        digest = hashlib.sha256(onnx_bytes + identity + Path(__file__).read_bytes()).hexdigest()
        cache = Path(os.environ.get("LOCALAPPDATA", str(Path.home()/".cache")))/"QFT-Plus"/"inference"
        cache.mkdir(parents=True, exist_ok=True)
        plan = cache/(digest+".engine")
        self.runtime = trt.Runtime(_trt_logger)
        self.engine = None
        if plan.exists():
            self.engine = self.runtime.deserialize_cuda_engine(plan.read_bytes())
        if self.engine is None:
            print("INFERENCE_PREPARING TensorRT tongue " + precision + " (cached after compilation)", flush=True)
            builder = trt.Builder(_trt_logger)
            network = builder.create_network(0)
            parser = trt.OnnxParser(network, _trt_logger)
            if not parser.parse(onnx_bytes):
                raise RuntimeError("; ".join(str(parser.get_error(i)) for i in range(parser.num_errors)))
            config = builder.create_builder_config()
            config.set_memory_pool_limit(trt.MemoryPoolType.WORKSPACE, 256*1024*1024)
            config.clear_flag(trt.BuilderFlag.TF32)
            if precision == "mixed":
                config.set_flag(trt.BuilderFlag.FP16)
                config.set_flag(trt.BuilderFlag.OBEY_PRECISION_CONSTRAINTS)
                protected_inputs = 0
                for i in range(network.num_layers):
                    layer = network.get_layer(i)
                    first_conv = "/encoder/network/network.0/Conv" in layer.name
                    protected_inputs += int(first_conv)
                    if (first_conv or "/head/" in layer.name) and layer.type != trt.LayerType.CONSTANT:
                        layer.precision = trt.float32
                        for j in range(layer.num_outputs):
                            if layer.get_output(j).dtype in (trt.float16, trt.float32):
                                layer.set_output_type(j, trt.float32)
                if protected_inputs != 2:
                    raise RuntimeError("TensorRT tongue input precision constraints did not match")
            serialized = builder.build_serialized_network(network, config)
            if serialized is None:
                raise RuntimeError("TensorRT tongue compilation failed")
            self.engine = self.runtime.deserialize_cuda_engine(serialized)
            if self.engine is None:
                raise RuntimeError("TensorRT tongue engine did not initialize")
            with tempfile.NamedTemporaryFile(dir=cache, suffix=".tmp", delete=False) as pending:
                temporary = Path(pending.name)
                pending.write(bytes(serialized))
            try:
                temporary.replace(plan)
            finally:
                temporary.unlink(missing_ok=True)
        self.context = self.engine.create_execution_context()
        if self.context is None:
            raise RuntimeError("TensorRT tongue context did not initialize")
        self.output = torch.empty(tuple(self.context.get_tensor_shape("values")), dtype=self.dtype, device=device)
        if not self.context.set_tensor_address("values", self.output.data_ptr()):
            raise RuntimeError("TensorRT tongue output binding failed")
        print("INFERENCE_BACKEND TensorRT tongue " + precision, flush=True)

    def forward(self, inputs):
        self.input = inputs.to(self.dtype)
        if not self.context.set_tensor_address("cameras", self.input.data_ptr()):
            raise RuntimeError("TensorRT tongue input binding failed")
        if not self.context.execute_async_v3(torch.cuda.current_stream(self.input.device).cuda_stream):
            raise RuntimeError("TensorRT tongue execution failed")
        return self.output.float()


def prepare_tongue_pair(gate, direction, size):
    """Use one graph, with optional compiled execution and FP32 fallback."""
    with _cuda_lock, torch.inference_mode():
        pair = TonguePair(optimize_for_inference(gate), optimize_for_inference(direction)).eval()
        precision = os.environ.get("QFT_TONGUE_TENSORRT", "mixed")
        if precision not in ("off", "fp32", "mixed"):
            raise ValueError("QFT_TONGUE_TENSORRT must be off, fp32 or mixed")
        if precision == "mixed" and any(type(model).__name__ != "SpatialStereoTongueModel" for model in (gate, direction)):
            precision = "fp32"
        if precision != "off":
            try:
                compiled = TensorRTTongue(pair, size, precision)
                return CudaModel(AreaResizeModel(compiled, size, 2).to(next(pair.parameters()).device).eval(), (400, 800))
            except Exception as error:
                print("INFERENCE_OPTIMIZATION_SKIPPED TensorRT tongue: " + str(error), flush=True)
        return CudaModel(AreaResizeModel(pair, size, 2).to(next(pair.parameters()).device).eval(), (400, 800))


def optimize_for_inference(model):
    """Fold only eval-mode BatchNorm; preserve checkpoint/training state layouts."""
    if model.training:
        raise ValueError("Inference optimization requires model.eval()")
    for child in model.children():
        optimize_for_inference(child)
    if isinstance(model, torch.nn.Sequential):
        for i in range(len(model) - 1):
            if isinstance(model[i], torch.nn.Conv2d) and isinstance(model[i + 1], torch.nn.BatchNorm2d):
                model[i] = torch.nn.utils.fuse_conv_bn_eval(model[i], model[i + 1])
                model[i + 1] = torch.nn.Identity()
    if hasattr(model, "batch_views"):
        model.batch_views = True
    return model


def select_device(requested="auto"):
    requested = os.environ.get("QFT_INFERENCE", "auto") if requested == "auto" else requested
    if requested not in ("auto", "cpu", "directml", "cuda", "cuda:0"):
        raise ValueError("Unknown inference device: " + requested)
    if requested == "auto":
        if torch.cuda.is_available():
            return "cuda:0"
        try:
            import onnxruntime as ort
            if "DmlExecutionProvider" in ort.get_available_providers():
                return "directml"
        except ImportError:
            pass
        return "cpu"
    return requested


class DirectMLModel:
    def __init__(self, model, checkpoint, shape, precision="fp32"):
        import onnxruntime as ort
        if "DmlExecutionProvider" not in ort.get_available_providers():
            raise RuntimeError("DirectML is unavailable")
        checkpoint = Path(checkpoint)
        self.raw_input = getattr(model, "raw_input", False)
        if precision not in ("fp32", "mixed") or (precision == "mixed" and
                (self.raw_input or tuple(shape) != (1, 2, 224, 224))):
            raise ValueError("DirectML mixed precision is validated only for 224-pixel tongue inputs")
        self.precision = precision
        source_model = model.model if self.raw_input else model
        example = torch.zeros(shape, dtype=torch.uint8 if self.raw_input else torch.float32)

        digest = hashlib.sha256(checkpoint.read_bytes() + Path(__file__).read_bytes()
            + Path(sys.modules[type(source_model).__module__].__file__).read_bytes()
            + repr((shape, precision)).encode()).hexdigest()
        cache = checkpoint.with_suffix(".area.onnx" if self.raw_input else
                                       ".mixed.onnx" if precision == "mixed" else ".onnx")
        stamp = cache.with_suffix(".onnx.sha256")
        if not cache.exists() or not stamp.exists() or stamp.read_text() != digest:
            pending = cache.with_suffix(".onnx.tmp")
            with torch.inference_mode():
                torch.onnx.export(model.cpu().eval(), example, str(pending),
                                  input_names=["cameras"], opset_version=17, dynamo=False)
            if precision == "mixed":
                import onnx
                from onnxruntime.transformers.float16 import convert_float_to_float16
                exported = onnx.load(pending)
                protected = [node.name for node in exported.graph.node
                             if "/encoder/network/network.0/Conv" in node.name or "/head/" in node.name]
                if sum("/encoder/network/network.0/Conv" in name for name in protected) != 1:
                    raise ValueError("Unrecognized tongue graph; keeping FP32")
                onnx.save(convert_float_to_float16(exported, keep_io_types=True,
                                                   node_block_list=protected), pending)
            pending.replace(cache)
            stamp.write_text(digest)
        options = ort.SessionOptions()
        options.enable_mem_pattern = False
        options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        self.session = ort.InferenceSession(str(cache), sess_options=options,
            providers=[("DmlExecutionProvider", {"device_id": max(0, min(15, int(os.environ.get("QFT_GPU_INDEX", "0"))))}), "CPUExecutionProvider"])
        if self.session.get_providers()[0] != "DmlExecutionProvider":
            raise RuntimeError("DirectML did not initialize")
        self.model = model
        self.failed = False
        self(example)
        if self.failed:
            raise RuntimeError("DirectML warm-up failed; using CPU")

    def __call__(self, inputs):
        if not self.failed:
            try:
                values = self.session.run(None, {"cameras": inputs.cpu().numpy()})
                tensors = tuple(torch.from_numpy(value) for value in values)
                return tensors[0] if len(tensors) == 1 else tensors
            except Exception as error:
                self.failed = True
                print("INFERENCE_FALLBACK cpu: " + str(error), flush=True)
        return self.model(inputs.cpu())


def prepare_model(model, checkpoint, shape, selected):
    model = optimize_for_inference(model)
    raw_model = None

    if (selected != "cpu" and len(shape) == 4 and shape[0] == 1 and shape[1] in (2, 5)
            and shape[2] == shape[3] and shape[2] in (64, 96, 128, 160, 192, 224, 256)):
        raw_model = AreaResizeModel(model, shape[2], shape[1]).eval().to(next(model.parameters()).device)
    raw_shape = (400, 400*shape[1])
    if selected.startswith("cuda"):
        return CudaModel(raw_model, raw_shape) if raw_model is not None else CudaModel(model, shape)
    if selected == "directml":
        try:
            result = None


            if (type(model).__name__ == "SpatialStereoTongueModel"
                    and tuple(shape) == (1, 2, 224, 224)
                    and os.environ.get("QFT_DIRECTML_PRECISION", "mixed") == "mixed"):
                try:
                    result = DirectMLModel(model, checkpoint, shape, precision="mixed")
                except Exception as error:
                    print("INFERENCE_OPTIMIZATION_SKIPPED DirectML mixed precision: " + str(error), flush=True)
            if result is None:
                result = DirectMLModel(model, checkpoint, shape)
            if raw_model is not None:
                try:
                    candidate = DirectMLModel(raw_model, checkpoint, raw_shape)
                    strip = np.zeros(raw_shape, np.uint8)
                    timings = [[], []]
                    with torch.inference_mode():
                        for iteration in range(8):
                            for index, backend in enumerate((result, candidate)):
                                started = time.perf_counter()
                                backend(prepare_inputs(backend, strip, shape[2], shape[1]))
                                if iteration >= 3:
                                    timings[index].append(time.perf_counter()-started)

                    if not candidate.failed and (result.failed or
                            statistics.median(timings[1]) < .8*statistics.median(timings[0])):
                        result = candidate
                except Exception as error:
                    print("INFERENCE_OPTIMIZATION_SKIPPED area resize: " + str(error), flush=True)
            print("INFERENCE_BACKEND DirectML " + result.precision + " " + str(checkpoint), flush=True)
            print("INFERENCE_PREPROCESS " + ("GPU area" if result.raw_input else "OpenCV"), flush=True)
            return result
        except Exception as error:
            print("INFERENCE_FALLBACK cpu: " + str(error), flush=True)
    return model
