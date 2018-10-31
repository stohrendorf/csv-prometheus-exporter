FROM python:3

RUN mkdir /app
WORKDIR /app
ENV SCRAPECONFIG=/etc/scrapeconfig.yml

COPY requirements.txt ./
RUN pip3 install \
  --no-cache-dir \
  -r requirements.txt \
  --upgrade

COPY . .

EXPOSE 5000
CMD ["python3", "app.py"]
