#!/usr/bin/env python3
"""Measure isolated Linux server CPU/RSS without polling it during each sample.

CPU percent is relative to one logical core. These short samples are development
comparisons, not a leak/soak or hardware acceptance test.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', default='Source/MultiFunPlayer.Server/bin/Release/net10.0/MultiFunPlayer.Server.dll')
    parser.add_argument('--dotnet', default=str(Path.home() / '.dotnet/dotnet'))
    parser.add_argument('--seconds', type=float, default=15)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    if args.seconds <= 0:
        parser.error('--seconds must be positive')
    spec = importlib.util.spec_from_file_location('soak', Path(__file__).with_name('run-portable-soak.py'))
    soak = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(soak)
    server = Path(args.server).resolve()
    token = 'isolated-performance-test-token'
    url = f'http://127.0.0.1:{soak.free_port()}'
    report = {'revision': soak.repository_revision(Path(__file__).resolve().parents[1]),
              'serverSha256': soak.sha256_file(server), 'cpuPercentBasis': 'one logical core',
              'environment': {'system': os.uname().sysname, 'release': os.uname().release,
                              'logicalCores': os.cpu_count(), 'connectedBrowsers': 0}, 'scenarios': []}
    with tempfile.TemporaryDirectory(prefix='mfp-profile-') as directory:
        env = os.environ.copy()
        env.update({'ASPNETCORE_URLS': url, 'MFP_AUTH_TOKEN': token,
                    'Logging__LogLevel__Default': 'Warning'})
        for name in ('config', 'keys', 'scripts', 'logs'):
            root = Path(directory) / name
            root.mkdir()
            env[f'MFP_{name.upper()}_ROOT'] = str(root)
        with (Path(directory) / 'server.log').open('w') as log:
            process = subprocess.Popen([args.dotnet, str(server)], cwd=server.parent, env=env, stdout=log, stderr=log)
        request = lambda path, method='GET', payload=None: soak.request_json(url, path, token, method, payload)
        def sample(name):
            time.sleep(2)  # exclude command handling and first JIT work
            before_status = request('/api/v1/status')
            before_outputs = request('/api/v1/output-targets')
            rss0, cpu0 = soak.process_metrics(process.pid)
            start = time.monotonic()
            rss = [rss0]
            while time.monotonic() - start < args.seconds:
                time.sleep(min(0.5, max(0, args.seconds - (time.monotonic() - start))))
                rss.append(soak.process_metrics(process.pid)[0])
            rss1, cpu1 = soak.process_metrics(process.pid)
            elapsed = time.monotonic() - start
            after_status = request('/api/v1/status')
            after_outputs = request('/api/v1/output-targets')
            row = {'scenario': name, 'seconds': round(elapsed, 3), 'cpuPercent': round((cpu1 - cpu0) / elapsed * 100, 3),
                   'rssStartMiB': round(rss0 / 2**20, 3), 'rssEndMiB': round(rss1 / 2**20, 3),
                   'rssPeakMiB': round(max(rss) / 2**20, 3), 'revisions': after_status['revision'] - before_status['revision'],
                   'sentFrames': sum(x['sentFrames'] for x in after_outputs) - sum(x['sentFrames'] for x in before_outputs),
                   'failedFrames': sum(x['failedFrames'] for x in after_outputs)}
            report['scenarios'].append(row)
            print(json.dumps(row), flush=True)
        try:
            soak.wait_for_live(url, 30)
            sample('empty-idle')
            request('/api/v1/output-targets', 'POST', {'id': 'profile-file', 'name': 'Profile file', 'protocol': 'file', 'endpoint': 'profile.tcode',
                    'enabled': True, 'autoConnect': True, 'updateRateHz': 20})
            request('/api/v1/scripts/load', 'POST', {'name': 'profile.funscript', 'axis': 'L0',
                    'content': {'actions': [{'at': 0, 'pos': 0}, {'at': 600000, 'pos': 100}]}})
            sample('loaded-idle-connected-output')
            request('/api/v1/player/play', 'POST', {})
            sample('playing-20hz-file')
            request('/api/v1/player/pause', 'POST', {})
            sample('paused-connected-output')
        finally:
            report['gracefulStop'] = soak.stop_process(process)
            output = Path(args.output)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(report, indent=2) + '\n')


if __name__ == '__main__':
    main()
