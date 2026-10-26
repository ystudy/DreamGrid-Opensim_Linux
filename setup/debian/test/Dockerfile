# A systemd-booted Debian for the install smoke (setup/debian/test/smoke.sh). install.sh adds everything else.
ARG DEBIAN=12
FROM debian:${DEBIAN}
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends systemd systemd-sysv netcat-openbsd procps \
    && rm -rf /var/lib/apt/lists/*
# systemd shuts down cleanly on this signal (docker stop).
STOPSIGNAL SIGRTMIN+3
CMD ["/lib/systemd/systemd"]
