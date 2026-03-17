# NVIDIA CUDA 12.6 + cuDNN runtime — supports RTX 50-series (Blackwell) via driver compat.
# Requires NVIDIA Container Toolkit on the host: https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html
# Run with: docker run --gpus all -p 8756:8756 -v xray-cache:/cache ghcr.io/ahass-thedev/jellyfin-xray-sidecar:latest
FROM nvidia/cuda:12.6.3-cudnn-runtime-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive

# Python + OpenCV system deps
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-pip \
    python3-dev \
    libgl1 \
    libglib2.0-0 \
    libgomp1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip3 install --no-cache-dir -r requirements.txt

# Pre-download InsightFace buffalo_l models at build time so the container
# works offline at runtime. Uses CPU provider here (no GPU during build).
RUN python3 -c "\
from insightface.app import FaceAnalysis; \
a = FaceAnalysis(name='buffalo_l', providers=['CPUExecutionProvider']); \
a.prepare(ctx_id=-1, det_size=(640, 640)); \
print('InsightFace buffalo_l models downloaded OK')"

COPY main.py matcher.py ./

# Encoding cache persisted via volume mount
ENV CACHE_DIR=/cache
RUN mkdir -p /cache

EXPOSE 8756

HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=3 \
    CMD python3 -c "import urllib.request; urllib.request.urlopen('http://localhost:8756/health')"

CMD ["python3", "main.py", "--host", "0.0.0.0", "--port", "8756", "--cache-dir", "/cache"]
