"""Jellyfin API helper for integration test scripts.

Configuration via environment variables:
  JELLYFIN_URL     — Base URL of the Jellyfin server (default: http://127.0.0.1:8096)
  JELLYFIN_API_KEY — API key for authentication (required, no default)
"""
import json, os, sys, urllib.request

_url = os.environ.get('JELLYFIN_URL', 'http://127.0.0.1:8096').rstrip('/')
api_key = os.environ.get('JELLYFIN_API_KEY', '')
if not api_key:
    print('ERROR: JELLYFIN_API_KEY environment variable is not set.', file=sys.stderr)
    print('Create an API key in Jellyfin Dashboard -> API Keys and export it.', file=sys.stderr)
    sys.exit(1)


def api(path, data=None, method=None):
    req = urllib.request.Request(
        _url + '/' + path,
        data=None if data is None else json.dumps(data).encode(),
        headers={'X-Emby-Token': api_key, 'Content-Type': 'application/json'},
        method=method,
    )
    with urllib.request.urlopen(req, timeout=60) as res:
        raw = res.read()
        return json.loads(raw) if raw else None


pid = 'b8dc8a71-3d33-4e51-b4d6-8ea09f8db491'

if __name__ == '__main__':
    cfg = api('Plugins/' + pid + '/Configuration')
    cfg.update(DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media')
    api('Plugins/' + pid + '/Configuration', cfg, 'POST')
    print({k: cfg[k] for k in ['DryRun', 'MaxRepairsPerRun', 'RepairRoot']})
    print([(t['Name'], t['Id'], t['State']) for t in api('ScheduledTasks') if 'Media' in t['Name']])
