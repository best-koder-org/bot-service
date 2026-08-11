FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Optional: pymysql for DB inspection features on the dashboard
RUN pip install --no-cache-dir pymysql

COPY . .

EXPOSE 9091

CMD ["python", "-m", "bot_service"]
