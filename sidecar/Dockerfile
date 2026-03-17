FROM python:3.11-slim

# Build dependencies for dlib/face_recognition
RUN apt-get update && apt-get install -y --no-install-recommends \
    cmake \
    build-essential \
    libopenblas-dev \
    liblapack-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py matcher.py ./

# Encoding cache persisted via volume mount
ENV CACHE_DIR=/cache
RUN mkdir -p /cache

EXPOSE 8756

HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 \
    CMD python3 -c "import urllib.request; urllib.request.urlopen('http://localhost:8756/health')"

CMD ["python3", "main.py", "--host", "0.0.0.0", "--port", "8756", "--cache-dir", "/cache"]
