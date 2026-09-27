"""Optional exact area-resize kernel; uses Windows CUDA PyTorch's bundled NVRTC."""
import ctypes as ct
from functools import cache
import math
from pathlib import Path

import torch


_SOURCE = r'''
extern "C" __global__ void area_resize(
    const unsigned char* __restrict__ raw,
    const float* __restrict__ levels,
    float* __restrict__ output)
{
    const int index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= COUNT * SIZE * SIZE) return;
    const int camera = index / (SIZE * SIZE);
    const int y = index / SIZE % SIZE;
    const int x = index % SIZE;
    const int top = y * P, bottom = (y + 1) * P;
    const int left = x * P, right = (x + 1) * P;
    int sum = 0;
    for (int iy = top / Q; iy < (bottom + Q - 1) / Q; ++iy) {
        const int wy = min(bottom, (iy + 1) * Q) - max(top, iy * Q);
        for (int ix = left / Q; ix < (right + Q - 1) / Q; ++ix) {
            const int wx = min(right, (ix + 1) * Q) - max(left, ix * Q);
            sum += raw[iy * (COUNT * 400) + camera * 400 + ix] * wx * wy;
        }
    }
    const int pixel = (sum + (P * P) / 2) / (P * P);
    output[index] = levels[pixel];
}
'''


def _check(code, operation):
    if code:
        raise RuntimeError(f'{operation} failed: {code}')


@cache
def _libraries():

    lib = Path(torch.__file__).parent / 'lib'
    paths = sorted(p for p in lib.glob('nvrtc64*.dll') if '.alt.' not in p.name)
    if not paths:
        raise RuntimeError('Bundled Windows NVRTC unavailable')
    nvrtc, driver = ct.WinDLL(str(paths[0])), ct.WinDLL('nvcuda.dll')
    ptr = ct.c_void_p
    signatures = {
        'nvrtcCreateProgram': [ct.POINTER(ptr), ct.c_char_p, ct.c_char_p, ct.c_int, ptr, ptr],
        'nvrtcCompileProgram': [ptr, ct.c_int, ct.POINTER(ct.c_char_p)],
        'nvrtcGetProgramLogSize': [ptr, ct.POINTER(ct.c_size_t)],
        'nvrtcGetProgramLog': [ptr, ptr],
        'nvrtcGetPTXSize': [ptr, ct.POINTER(ct.c_size_t)],
        'nvrtcGetPTX': [ptr, ptr],
        'nvrtcDestroyProgram': [ct.POINTER(ptr)],
    }
    for name, args in signatures.items():
        getattr(nvrtc, name).argtypes = args
    driver.cuModuleLoadData.argtypes = [ct.POINTER(ptr), ptr]
    driver.cuModuleGetFunction.argtypes = [ct.POINTER(ptr), ptr, ct.c_char_p]
    driver.cuModuleUnload.argtypes = [ptr]
    driver.cuLaunchKernel.argtypes = [ptr] + [ct.c_uint]*7 + [ptr, ct.POINTER(ptr), ptr]
    return nvrtc, driver


@cache
def _compile(size, count, device):
    nvrtc, driver = _libraries()
    program = ct.c_void_p()
    _check(nvrtc.nvrtcCreateProgram(ct.byref(program), _SOURCE.encode(), b'area_resize.cu', 0, None, None), 'NVRTC create')
    try:
        major, minor = torch.cuda.get_device_capability(device)
        g = math.gcd(400, size)
        options = [f'--gpu-architecture=compute_{major}{minor}', f'-DSIZE={size}',
                   f'-DCOUNT={count}', f'-DP={400//g}', f'-DQ={size//g}']
        options = (ct.c_char_p*len(options))(*(s.encode() for s in options))
        error = nvrtc.nvrtcCompileProgram(program, len(options), options)
        if error:
            length = ct.c_size_t()
            _check(nvrtc.nvrtcGetProgramLogSize(program, ct.byref(length)), 'NVRTC log size')
            log = ct.create_string_buffer(length.value)
            _check(nvrtc.nvrtcGetProgramLog(program, log), 'NVRTC log')
            raise RuntimeError(log.value.decode(errors='replace'))
        length = ct.c_size_t()
        _check(nvrtc.nvrtcGetPTXSize(program, ct.byref(length)), 'NVRTC PTX size')
        ptx = ct.create_string_buffer(length.value)
        _check(nvrtc.nvrtcGetPTX(program, ptx), 'NVRTC PTX')
    finally:
        nvrtc.nvrtcDestroyProgram(ct.byref(program))
    module, function = ct.c_void_p(), ct.c_void_p()
    _check(driver.cuModuleLoadData(ct.byref(module), ptx), 'CUDA load module')
    try:
        _check(driver.cuModuleGetFunction(ct.byref(function), module, b'area_resize'), 'CUDA get function')
    except Exception:
        driver.cuModuleUnload(module)
        raise

    return driver, module, function


class CudaAreaResize:
    """Owns one output buffer, used under the inference backend's CUDA lock."""
    def __init__(self, size, count, device):
        if size not in (64, 96, 128, 160, 192, 224, 256) or count not in (2, 5):
            raise ValueError('Unsupported area resize shape')
        self.size, self.count = size, count
        with torch.cuda.device(device):
            self.output = torch.empty((1, count, size, size), device=device, dtype=torch.float32)
            self.driver, self.module, self.function = _compile(size, count, self.output.device.index)

    def __call__(self, raw, levels):
        if (raw.shape != (400, self.count*400) or raw.dtype != torch.uint8
                or raw.device != self.output.device or not raw.is_contiguous()
                or levels.shape != (256,) or levels.dtype != torch.float32
                or levels.device != raw.device or not levels.is_contiguous()):
            raise ValueError('CUDA area resize input shape, dtype or device changed')
        pointers = [ct.c_void_p(t.data_ptr()) for t in (raw, levels, self.output)]
        args = (ct.c_void_p*3)(*(ct.cast(ct.byref(p), ct.c_void_p) for p in pointers))
        with torch.cuda.device(raw.device):
            stream = ct.c_void_p(torch.cuda.current_stream(raw.device).cuda_stream)
            _check(self.driver.cuLaunchKernel(self.function, (self.count*self.size**2+127)//128,
                   1, 1, 128, 1, 1, 0, stream, args, None), 'CUDA area launch')
        return self.output
