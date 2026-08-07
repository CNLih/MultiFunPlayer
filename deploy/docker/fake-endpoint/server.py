import os
import socket
import threading


MODE = os.environ.get("MODE", "tcp").lower()
HOST = os.environ.get("HOST", "0.0.0.0")
PORT = int(os.environ.get("PORT", "18000"))


def log(payload, peer):
    print(f"received mode={MODE} peer={peer} bytes={len(payload)} payload={payload[:256]!r}", flush=True)


def tcp_server():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((HOST, PORT))
        server.listen(16)
        print(f"listening mode=tcp address={HOST}:{PORT}", flush=True)
        while True:
            connection, peer = server.accept()
            with connection:
                while True:
                    payload = connection.recv(65536)
                    if not payload:
                        break
                    log(payload, peer)


def udp_server():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
        server.bind((HOST, PORT))
        print(f"listening mode=udp address={HOST}:{PORT}", flush=True)
        while True:
            payload, peer = server.recvfrom(65536)
            log(payload, peer)


if MODE == "tcp":
    tcp_server()
elif MODE == "udp":
    udp_server()
else:
    raise SystemExit(f"Unsupported MODE: {MODE}")
