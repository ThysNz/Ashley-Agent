FROM python:3.11-slim
RUN pip install --no-cache-dir fastapi uvicorn faster-whisper
WORKDIR /app
COPY server.py .
ENV WHISPER_MODEL=base.en WHISPER_DEVICE=cpu WHISPER_COMPUTE=int8
EXPOSE 8000
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000"]
