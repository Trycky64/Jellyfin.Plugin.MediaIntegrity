"""Run on the Docker host with sudo, alongside pi_api.py. Only dedicated fixtures are replaced.

Configuration via environment variables:
  JELLYFIN_CONTAINER   — Docker container name (default: jellyfin)
  JELLYFIN_CONFIG_ROOT — Host path to Jellyfin config directory (required)
  JELLYFIN_MEDIA_ROOT  — Host path to media root (required)
  JELLYFIN_BACKUP_ROOT — Host path to backup root (required)
"""
import argparse, datetime, hashlib, json, os, pathlib, shutil, subprocess, sys, time, uuid
from pi_api import api, pid

parser = argparse.ArgumentParser()
parser.add_argument('--formats', default='mp4')
args = parser.parse_args()

container = os.environ.get('JELLYFIN_CONTAINER', 'jellyfin')
config_root = pathlib.Path(os.environ.get('JELLYFIN_CONFIG_ROOT', ''))
media_root = pathlib.Path(os.environ.get('JELLYFIN_MEDIA_ROOT', ''))
backup_root = pathlib.Path(os.environ.get('JELLYFIN_BACKUP_ROOT', ''))

for name, value in [('JELLYFIN_CONFIG_ROOT', config_root), ('JELLYFIN_MEDIA_ROOT', media_root), ('JELLYFIN_BACKUP_ROOT', backup_root)]:
    if not str(value):
        print(f'ERROR: {name} environment variable is not set.', file=sys.stderr)
        sys.exit(1)

queue_path = config_root / 'data/media-integrity/repair-queue.json'
run = 'release-' + uuid.uuid4().hex
relative = 'series/Media Integrity Test/' + run
media = '/media/' + relative
repair = '/repair-media/' + relative
backup = '/repair-backups/' + relative
host_media = media_root / relative
host_backup = backup_root / relative
saved = pathlib.Path('/tmp') / (run + '-queue.json')
shutil.copy2(queue_path, saved)
original_config = api('Plugins/' + pid + '/Configuration')
report = {'timestampUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'formats': {}, 'queueBackup': str(saved)}


def docker(*args):
    return subprocess.check_output(['docker', 'exec', container, *args], text=True)


def sha(path):
    return docker('sha256sum', path).split()[0].upper()


def signature(path):
    return json.loads(docker('/usr/lib/jellyfin-ffmpeg/ffprobe', '-v', 'error', '-show_entries',
                             'stream=index,codec_type,codec_name,width,height,sample_rate,channels', '-of', 'json', path))


def task(key):
    return next(t for t in api('ScheduledTasks') if t['Key'] == key)


def run_task(key):
    tid = task(key)['Id']
    api('ScheduledTasks/Running/' + tid, method='POST')
    time.sleep(.5)
    deadline = time.monotonic() + 600
    while task(key)['State'] != 'Idle':
        if time.monotonic() > deadline:
            raise TimeoutError(key)
        time.sleep(.5)
    result = task(key)['LastExecutionResult']
    assert result['Status'] == 'Completed', result
    return result


try:
    assert task('MediaRemuxRepair')['State'] == 'Idle'
    assert task('MediaIntegrityScan')['State'] == 'Idle'
    docker('mkdir', '-p', repair)
    cfg = dict(original_config, DryRun=False, MaxRepairsPerRun=1, RepairRoot='/repair-media', KeepBackups=False)
    api('Plugins/' + pid + '/Configuration', cfg, 'POST')
    for ext in args.formats.split(','):
        source = media + '/test.' + ext
        docker('cp', '/cache/media-integrity-fixtures/sources/test.' + ext, repair + '/test.' + ext)
        before = sha(source)
        streams = signature(source)
        for iteration in range(2 if ext == 'mp4' else 1):
            q = {'version': 1, 'generatedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'summary': {},
                 'files': [{'path': source, 'extension': '.' + ext, 'container': ext,
                            'integrityStatus': 'remuxRecommended', 'issues': [], 'status': 'pending', 'attempts': 0}]}
            queue_path.write_text(json.dumps(q))
            run_task('MediaRemuxRepair')
            result = json.loads(queue_path.read_text())['files'][0]
            assert result['status'] == 'repaired', result
        manifests = list(host_backup.glob('test*.metadata.json'))
        relevant = [p for p in manifests if p.name.endswith('.' + ext + '.metadata.json')]
        assert len(relevant) == (2 if ext == 'mp4' else 1), relevant
        for path in relevant:
            metadata = json.loads(path.read_text())
            assert metadata['sourceHash'] == metadata['backupHash'] == sha(metadata['backupPath'])
            assert metadata['repairedHash'] == sha(source)
            assert metadata['sourcePath'] == source
            assert metadata['repairedPath'] == repair + '/test.' + ext
        assert signature(source) == streams
        assert json.loads((host_backup / ('test.' + ext + '.metadata.json')).read_text())['backupHash'] == before
        report['formats'][ext] = {'backupMetadata': True, 'hashes': True, 'streamsPreserved': True,
                                  'collision': ext == 'mp4', 'originalHash': before, 'repairedHash': sha(source)}
    # Restart with safe settings, then verify durable evidence.
    cfg.update(DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media')
    api('Plugins/' + pid + '/Configuration', cfg, 'POST')
    queue_hash = hashlib.sha256(queue_path.read_bytes()).hexdigest()
    subprocess.check_call(['docker', 'restart', container], stdout=subprocess.DEVNULL)
    for i in range(90):
        try:
            api('System/Info')
            break
        except Exception:
            time.sleep(1)
    else:
        raise TimeoutError('Jellyfin restart')
    assert queue_hash == hashlib.sha256(queue_path.read_bytes()).hexdigest()
    assert all(p.exists() for p in manifests)
    report['restartPersistence'] = True
    report['mounts'] = json.loads(subprocess.check_output(['docker', 'inspect', container, '--format', '{{json .Mounts}}']))
    assert all(not m['RW'] for m in report['mounts'] if m['Destination'].startswith('/media/'))
    report['mediaReadOnly'] = True
finally:
    cfg = dict(original_config, DryRun=True, MaxRepairsPerRun=1, RepairRoot='/repair-media')
    api('Plugins/' + pid + '/Configuration', cfg, 'POST')
    shutil.copy2(saved, queue_path)
    # These unique directories were created by this run; never delete other backups.
    for owned in [host_media, host_backup]:
        assert owned.name == run and owned.parent.name == 'Media Integrity Test'
        if owned.exists():
            shutil.rmtree(owned)
    report['safeConfiguration'] = True
    report['queueRestored'] = queue_path.read_bytes() == saved.read_bytes()
    report['fixturesRemoved'] = not host_media.exists() and not host_backup.exists()
    print(json.dumps(report, indent=2))
