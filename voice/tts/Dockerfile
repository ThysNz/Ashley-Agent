FROM python:3.11-slim
RUN pip install --no-cache-dir fastapi uvicorn piper-tts
WORKDIR /app
COPY server.py .
ENV VOICE=en_US-hfc_female-medium
EXPOSE 8000
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000"]
