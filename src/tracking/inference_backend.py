"""Optional DirectML inference; CPU training and fallback work on every PC."""
import hashlib
import io
import math
import os
import statistics
import sys
import threading
import time
from pathlib import Path
from functools import lru_cache
import numpy as np
import torch
from prepare_training import resize_cameras
from gpu_lock import GPU_LOCK




_gpu_lock = GPU_LOCK  # shared with the universal model (gpu_lock.py)


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
        with _gpu_lock, torch.inference_mode():
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
        with _gpu_lock, torch.inference_mode():
            self.inputs.copy_(inputs)
            if self.graph is not None:
                self.graph.replay()
                output = self.output
            else:
                output = self._forward()

            values = output.cpu()
            return torch.split(values, self.widths, dim=1) if self.widths is not None else values


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


@lru_cache(maxsize=16)
def directml_vendor(device_id):
    if os.name != "nt":
        return None
    import ctypes
    import uuid
    class Description(ctypes.Structure):
        _fields_ = [("name", ctypes.c_wchar*128), ("vendor", ctypes.c_uint32),
                    ("device", ctypes.c_uint32), ("subsystem", ctypes.c_uint32), ("revision", ctypes.c_uint32),
                    ("video", ctypes.c_size_t), ("system", ctypes.c_size_t), ("shared", ctypes.c_size_t),
                    ("luid_low", ctypes.c_uint32), ("luid_high", ctypes.c_int32), ("flags", ctypes.c_uint32)]
    def method(obj, slot, *types):
        address = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents[slot]
        return ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, *types)(address)
    factory, adapter = ctypes.c_void_p(), ctypes.c_void_p()
    try:
        iid = (ctypes.c_byte*16).from_buffer_copy(uuid.UUID("770aae78-f26f-4dba-a829-253c83d1b387").bytes_le)
        create = ctypes.WinDLL("dxgi").CreateDXGIFactory1
        create.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
        create.restype = ctypes.c_long
        if create(ctypes.byref(iid), ctypes.byref(factory)) < 0:
            return None
        if method(factory, 12, ctypes.c_uint, ctypes.POINTER(ctypes.c_void_p))(factory, device_id, ctypes.byref(adapter)) < 0:
            return None
        description = Description()
        if method(adapter, 10, ctypes.POINTER(Description))(adapter, ctypes.byref(description)) >= 0:
            return description.vendor
    except (OSError, ValueError):
        return None
    finally:
        for obj in (adapter, factory):
            if obj.value:
                method(obj, 2)(obj)
    return None


class DirectMLModel:
    def __init__(self, model, checkpoint, shape, precision="fp32"):
        import onnxruntime as ort
        if "DmlExecutionProvider" not in ort.get_available_providers():
            raise RuntimeError("DirectML is unavailable")
        checkpoint = Path(checkpoint)
        self.raw_input = getattr(model, "raw_input", False)
        source_model = model.model if self.raw_input else model
        neural_shape = (1, model.count, model.size, model.size) if self.raw_input else tuple(shape)
        if precision not in ("fp32", "mixed") or (precision == "mixed" and
                neural_shape != (1, 2, 224, 224)):
            raise ValueError("DirectML mixed precision is validated only for 224-pixel tongue inputs")
        self.precision = precision
        example = torch.zeros(shape, dtype=torch.uint8 if self.raw_input else torch.float32)
        sources = (source_model,)
        digest = hashlib.sha256(checkpoint.read_bytes() + Path(__file__).read_bytes()
            + b"".join(Path(sys.modules[type(m).__module__].__file__).read_bytes() for m in sources)
            + repr((shape, precision)).encode()).hexdigest()
        cache = checkpoint.with_suffix((".area" if self.raw_input else "") +
                                       (".mixed" if precision == "mixed" else "") + ".onnx")
        stamp = cache.with_suffix(".onnx.sha256")
        if not cache.exists() or not stamp.exists() or stamp.read_text() != digest:
            pending = cache.with_suffix(".onnx.tmp")
            with torch.inference_mode():
                export_model = source_model if precision == "mixed" else model
                export_input = torch.zeros(neural_shape) if precision == "mixed" else example
                torch.onnx.export(export_model.cpu().eval(), export_input, str(pending),
                                  input_names=["cameras"], opset_version=17, dynamo=False)
            if precision == "mixed":
                import onnx
                from onnxruntime.transformers.float16 import convert_float_to_float16
                from onnxruntime.transformers.onnx_model import OnnxModel
                exported = onnx.load(pending)
                protected = [node.name for node in exported.graph.node
                             if "/encoder/network/network.0/Conv" in node.name or "/head/" in node.name]
                if sum("/encoder/network/network.0/Conv" in name for name in protected) != len(sources):
                    raise ValueError("Unrecognized tongue graph; keeping FP32")
                exported = convert_float_to_float16(exported, keep_io_types=True, node_block_list=protected)
                OnnxModel(exported).topological_sort()
                if self.raw_input:
                    resized = io.BytesIO()
                    with torch.inference_mode():
                        torch.onnx.export(AreaResizeModel(torch.nn.Identity(), model.size, model.count).eval(),
                            example, resized, input_names=["cameras"], output_names=["resized"],
                            opset_version=17, dynamo=False)
                    preprocessing = onnx.compose.add_prefix(onnx.load_model_from_string(resized.getvalue()),
                                                           "preprocess/", rename_inputs=False)
                    exported = onnx.compose.merge_models(preprocessing, onnx.compose.add_prefix(exported, "network/"),
                                                        io_map=[("preprocess/resized", "network/cameras")])
                onnx.checker.check_model(exported)
                onnx.save(exported, pending)
            pending.replace(cache)
            stamp.write_text(digest)
        options = ort.SessionOptions()
        options.enable_mem_pattern = False
        options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        with _gpu_lock:
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
                with _gpu_lock:
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


            precision = os.environ.get("QFT_DIRECTML_PRECISION")
            if precision is None:
                adapter = max(0, min(15, int(os.environ.get("QFT_GPU_INDEX", "0"))))
                precision = "mixed" if directml_vendor(adapter) == 0x10DE else "fp32"
            if (type(model).__name__ == "SpatialStereoTongueModel"
                    and tuple(shape) == (1, 2, 224, 224)
                    and precision == "mixed"):
                try:
                    result = DirectMLModel(model, checkpoint, shape, precision="mixed")
                except Exception as error:
                    print("INFERENCE_OPTIMIZATION_SKIPPED DirectML mixed precision: " + str(error), flush=True)
            if result is None:
                result = DirectMLModel(model, checkpoint, shape)
            if raw_model is not None:
                try:
                    candidate = DirectMLModel(raw_model, checkpoint, raw_shape, precision="mixed") if result.precision == "mixed" else DirectMLModel(raw_model, checkpoint, raw_shape)
                    strip = np.zeros(raw_shape, np.uint8)
                    timings = [[], []]
                    with torch.inference_mode():
                        for iteration in range(24):
                            due = time.perf_counter() + 1/24
                            for index, backend in enumerate((result, candidate)):
                                started = time.perf_counter()
                                backend(prepare_inputs(backend, strip, shape[2], shape[1]))
                                if iteration >= 4:
                                    timings[index].append(time.perf_counter()-started)
                            time.sleep(max(0, due-time.perf_counter()))

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
