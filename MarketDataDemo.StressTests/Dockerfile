FROM grafana/k6:latest

WORKDIR /scripts
COPY candles-stress.js .

ENTRYPOINT ["k6", "run"]
CMD ["/scripts/candles-stress.js"]
