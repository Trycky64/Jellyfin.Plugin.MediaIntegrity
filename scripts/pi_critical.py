"""Critical scheduled-task checks against dedicated files, without encoding.

Configuration via environment variables:
  JELLYFIN_CONTAINER   — Docker container name (default: jellyfin)
  JELLYFIN_CONFIG_ROOT — Host path to Jellyfin config directory (required)
  JELLYFIN_MEDIA_ROOT  — Host path to media root (required)
  JELLYFIN_BACKUP_ROOT — Host path to backup root (required)
"""
import datetime, hashlib, json, os, pathlib, shutil, subprocess, sys, time, uuid
from pi_api import api, pid

for i in range(90):
    try:
        api('System/Info')
        break
    except Exception:
        time.sleep(1)

container = os.environ.get('JELLYFIN_CONTAINER', 'jellyfin')
config_root = pathlib.Path(os.environ.get('JELLYFIN_CONFIG_ROOT', ''))
media_root = pathlib.Path(os.environ.get('JELLYFIN_MEDIA_ROOT', ''))
backup_root = pathlib.Path(os.environ.get('JELLYFIN_BACKUP_ROOT', ''))

for name, value in [('JELLYFIN_CONFIG_ROOT', config_root), ('JELLYFIN_MEDIA_ROOT', media_root), ('JELLYFIN_BACKUP_ROOT', backup_root)]:
    if not str(value):
        print(f'ERROR: {name} environment variable is not set.', file=sys.stderr)
        sys.exit(1)

root = config_root / 'data/media-integrity'
queue = root / 'repair-queue.json'
run = 'critical-' + uuid.uuid4().hex
saved = pathlib.Path('/tmp') / (run + '-queue.json')
shutil.copy2(queue, saved)
config = api('Plugins/' + pid + '/Configuration')
rel = 'series/Media Integrity Test/' + run
host = media_root / rel
backup = backup_root / rel
source = '/media/' + rel + '/unreadable.mp4'


def docker(*args):
    return subprocess.check_output(['docker', 'exec', container, *args], text=True)


def task(key):
    return next(t for t in api('ScheduledTasks') if t['Key'] == key)


def wait(key):
    start = time.monotonic()
    while task(key)['State'] != 'Idle':
        assert time.monotonic() - start < 120
        time.sleep(.2)
    return task(key)['LastExecutionResult']


def run_task(key):
    api('ScheduledTasks/Running/' + task(key)['Id'], method='POST')
    time.sleep(.2)
    return wait(key)


def inject(path, status='remuxRecommended', attempts=0):
    queue.write_text(json.dumps({'version': 1,
                                 'generatedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                                 'summary': {},
                                 'files': [{'path': path, 'extension': '.mp4', 'integrityStatus': status,
                                            'status': 'pending', 'attempts': attempts, 'issues': []}]}))


report = {}
try:
    host.mkdir(parents=True)
    (host / 'unreadable.mp4').write_bytes(b'This is a deliberately unreadable test file.')
    before = (host / 'unreadable.mp4').read_bytes()
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=False, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')
    inject(source)
    for expected in [1, 2, 3, 3]:
        run_task('MediaRemuxRepair')
        item = json.loads(queue.read_text())['files'][0]
        assert item['status'] == 'failed' and item['attempts'] == expected, item
    assert before == (host / 'unreadable.mp4').read_bytes()
    assert not backup.exists()
    report['retryCap'] = 3
    report['unreadableNeverReplaced'] = True
    inject('/etc/passwd')
    run_task('MediaRemuxRepair')
    item = json.loads(queue.read_text())['files'][0]
    assert item['status'] == 'failed' and item['attempts'] == 0 and 'outside allowed root' in item['lastError'], item
    report['hostileQueueRejectedBeforeRemux'] = True
    docker('cp', '/cache/media-integrity-fixtures/sources/test.mp4', '/repair-media/' + rel + '/dryrun.mp4')
    dry = '/media/' + rel + '/dryrun.mp4'
    before = (host / 'dryrun.mp4').read_bytes()
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')
    inject(dry)
    run_task('MediaRemuxRepair')
    item = json.loads(queue.read_text())['files'][0]
    assert item['status'] == 'pending' and item['attempts'] == 0, item
    assert before == (host / 'dryrun.mp4').read_bytes()
    report['dryRunPreservesMediaAndAttempts'] = True
    # Healthy items are never selected for replacement, even with real repair enabled.
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=False, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')
    for extension in ['mkv', 'm4a']:
        name = 'healthy.' + extension
        docker('cp', '/cache/media-integrity-fixtures/sources/test.' + extension, '/repair-media/' + rel + '/' + name)
        healthy = '/media/' + rel + '/' + name
        before = (host / name).read_bytes()
        inject(healthy, status='ok')
        run_task('MediaRemuxRepair')
        item = json.loads(queue.read_text())['files'][0]
        assert item['status'] == 'pending' and item['attempts'] == 0, item
        assert before == (host / name).read_bytes()
        report['healthy' + extension.upper() + 'Unchanged'] = True
    # Source symlink rejection: no process attempt and no write outside fixture roots.
    docker('ln', '-s', '/etc/passwd', '/repair-media/' + rel + '/symlink.mp4')
    inject('/media/' + rel + '/symlink.mp4')
    run_task('MediaRemuxRepair')
    item = json.loads(queue.read_text())['files'][0]
    assert item['status'] == 'failed' and item['attempts'] == 0 and 'symbolic link' in item['lastError'], item
    report['sourceSymlinkRejected'] = True
    (host / 'symlink.mp4').unlink()
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')

    # Cancellation of a real full-library scan retains the previous queue/statistics.
    prior = queue.read_bytes()
    prior_stats = (root / 'last-scan.json').read_bytes()
    api('ScheduledTasks/Running/' + task('MediaIntegrityScan')['Id'], method='POST')
    time.sleep(.5)
    api('ScheduledTasks/Running/' + task('MediaIntegrityScan')['Id'], method='DELETE')
    result = wait('MediaIntegrityScan')
    assert result['Status'] == 'Cancelled', result
    assert prior == queue.read_bytes()
    assert prior_stats == (root / 'last-scan.json').read_bytes()
    report['scanCancellationPreservesCompletedData'] = True
    # Starting repair during a scan must wait without altering the queue.
    api('ScheduledTasks/Running/' + task('MediaIntegrityScan')['Id'], method='POST')
    time.sleep(.3)
    api('ScheduledTasks/Running/' + task('MediaRemuxRepair')['Id'], method='POST')
    time.sleep(.3)
    assert queue.read_bytes() == prior
    api('ScheduledTasks/Running/' + task('MediaRemuxRepair')['Id'], method='DELETE')
    assert wait('MediaRemuxRepair')['Status'] == 'Cancelled'
    api('ScheduledTasks/Running/' + task('MediaIntegrityScan')['Id'], method='DELETE')
    wait('MediaIntegrityScan')
    report['concurrentRepairWaitsAndCancels'] = True
finally:
    api('Plugins/' + pid + '/Configuration', dict(config, DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media'), 'POST')
    shutil.copy2(saved, queue)
    for owned in [host, backup]:
        assert owned.name == run and owned.parent.name == 'Media Integrity Test'
        if owned.exists():
            shutil.rmtree(owned)
    report['safeConfigurationRestored'] = True
    report['queueRestored'] = saved.read_bytes() == queue.read_bytes()
    report['fixturesRemoved'] = not host.exists() and not backup.exists()
    print(json.dumps(report, indent=2))
