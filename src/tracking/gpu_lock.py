"""One lock for every GPU inference session in the tracking process (universal model and older per-user models).

Creating, running or dropping a DirectML session while another thread runs one crashes the NVIDIA driver
(nvwgf2umx.dll access violation): Studio reloads build the next model in a worker thread while frames keep running.
"""
import threading

GPU_LOCK = threading.RLock()
