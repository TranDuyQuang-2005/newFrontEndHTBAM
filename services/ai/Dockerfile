FROM python:3.11-slim
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg libgl1 libglib2.0-0 build-essential gcc g++ libgles2 libegl1 && rm -rf /var/lib/apt/lists/*
COPY requirements.txt .
RUN python -m pip install --no-cache-dir --upgrade pip && \
    pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cu130 torch==2.14.1 torchvision==0.29.1 && \
    pip install --no-cache-dir -r requirements.txt
RUN (pip uninstall -y onnxruntime onnxruntime-gpu || true) && \
    pip install --no-cache-dir onnxruntime-gpu==1.31.0

COPY app ./app
COPY vendor ./vendor
ENV PYTHONPATH="/app/vendor:/app"
CMD ["uvicorn","app.main:app","--host","0.0.0.0","--port","8001"]
