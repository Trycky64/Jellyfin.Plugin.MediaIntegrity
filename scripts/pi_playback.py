"""Validate a real Jellyfin playback session against a disposable indexed fixture.

Configuration via environment variables:
  JELLYFIN_CONTAINER   — Docker container name (default: jellyfin)
  JELLYFIN_MEDIA_ROOT  — Host path to media root (required)
  JELLYFIN_CONFIG_ROOT — Host path to Jellyfin config directory (required)
  JELLYFIN_URL         — Jellyfin server URL (default: http://127.0.0.1:8096, via pi_api)
  JELLYFIN_API_KEY     — API key for authentication (required, via pi_api)
"""
import datetime, json, os, pathlib, shutil, subprocess, sys, time, urllib.parse, urllib.request, uuid
from pi_api import api, pid, api_key, _url

container = os.environ.get('JELLYFIN_CONTAINER', 'jellyfin')
media_root = pathlib.Path(os.environ.get('JELLYFIN_MEDIA_ROOT', ''))
config_root = pathlib.Path(os.environ.get('JELLYFIN_CONFIG_ROOT', ''))

for name, value in [('JELLYFIN_MEDIA_ROOT', media_root), ('JELLYFIN_CONFIG_ROOT', config_root)]:
    if not str(value):
        print(f'ERROR: {name} environment variable is not set.', file=sys.stderr)
        sys.exit(1)

run = 'playback-' + uuid.uuid4().hex
relative = 'Movies/Media Integrity Test/' + run
host = media_root / relative
source = '/media/' + relative + '/Media Integrity Playback (2026).mp4'
queue = config_root / 'data/media-integrity/repair-queue.json'
saved = pathlib.Path('/tmp') / (run + '-queue.json')
shutil.copy2(queue, saved)
config = api('Plugins/' + pid + '/Configuration')
item = None
playing = False
report = {}


def task(key):
    return next(t for t in api('ScheduledTasks') if t['Key'] == key)


def wait(key):
    start = time.monotonic()
    while task(key)['State'] != 'Idle':
        if time.monotonic() - start > 180:
            raise TimeoutError(key)
        time.sleep(.2)
    return task(key)['LastExecutionResult']


try:
    assert not any(s.get('NowPlayingItem') for s in api('Sessions')), 'Existing playback; do not interfere'
    host.mkdir(parents=True)
    subprocess.check_call(['docker', 'exec', container, 'cp',
                           '/cache/media-integrity-fixtures/sources/test.mp4',
                           '/repair-media/' + relative + '/Media Integrity Playback (2026).mp4'])
    api('Library/Refresh', method='POST')
    for i in range(120):
        items = api('Items?Recursive=true&Fields=Path&Limit=10000')['Items']
        item = next((x for x in items if x.get('Path') == source), None)
        if item:
            break
        time.sleep(1)
    assert item, 'Fixture was not indexed'
    queue.write_text(json.dumps({'version': 1,
                                 'generatedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                                 'summary': {},
                                 'files': [{'path': source, 'extension': '.mp4', 'integrityStatus': 'remuxRecommended',
                                            'status': 'pending', 'attempts': 0, 'issues': []}]}))
    api('Sessions/Playing', {'ItemId': item['Id'], 'CanSeek': True, 'PlayMethod': 'DirectPlay', 'PositionTicks': 0}, 'POST')
    playing = True
    sessions = api('Sessions')
    assert any(s.get('NowPlayingItem', {}).get('Id') == item['Id'] for s in sessions), sessions
    before = (host / 'Media Integrity Playback (2026).mp4').read_bytes()
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=False, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')
    api('ScheduledTasks/Running/' + task('MediaRemuxRepair')['Id'], method='POST')
    time.sleep(.3)
    assert wait('MediaRemuxRepair')['Status'] == 'Completed'
    result = json.loads(queue.read_text())['files'][0]
    assert result['status'] == 'pending' and result['attempts'] == 0, result
    assert before == (host / 'Media Integrity Playback (2026).mp4').read_bytes()
    report['activeJellyfinSessionSkippedWithoutAttempt'] = True
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')
    api('ScheduledTasks/Running/' + task('MediaIntegrityScan')['Id'], method='POST')
    time.sleep(.3)
    req = urllib.request.Request(_url + '/Videos/' + item['Id'] + '/stream?static=true',
                                headers={'X-Emby-Token': api_key})
    with urllib.request.urlopen(req, timeout=60) as response:
        content = response.read()
    assert content == before
    report['directPlayBytesDuringScan'] = len(content)
    assert wait('MediaIntegrityScan')['Status'] == 'Completed'
finally:
    if playing:
        api('Sessions/Playing/Stopped', {'ItemId': item['Id'], 'PositionTicks': 0}, 'POST')
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')
    shutil.copy2(saved, queue)
    if host.exists():
        assert host.name == run and host.parent.name == 'Media Integrity Test'
        shutil.rmtree(host)
    api('Library/Refresh', method='POST')
    report['safeConfigurationRestored'] = True
    report['fixtureRemoved'] = not host.exists()
    report['queueRestored'] = queue.read_bytes() == saved.read_bytes()
    print(json.dumps(report, indent=2))
