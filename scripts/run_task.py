"""Trigger a scheduled task by name via the real Jellyfin API and wait for completion."""
import sys
import time
from pi_api import api


def find_task(name):
    return next(t for t in api('ScheduledTasks') if t['Name'] == name)


def run_task(name, timeout=1800):
    task = find_task(name)
    if task['State'] != 'Idle':
        raise RuntimeError(f"Task '{name}' is not Idle: {task['State']}")
    api('ScheduledTasks/Running/' + task['Id'], method='POST')
    time.sleep(1)
    deadline = time.monotonic() + timeout
    while True:
        task = find_task(name)
        if task['State'] == 'Idle':
            break
        if time.monotonic() > deadline:
            raise TimeoutError(f"Task '{name}' did not complete within {timeout}s")
        time.sleep(2)
    result = task['LastExecutionResult']
    return result


if __name__ == '__main__':
    name = sys.argv[1]
    timeout = int(sys.argv[2]) if len(sys.argv) > 2 else 1800
    result = run_task(name, timeout)
    print('Status:', result['Status'])
    print('StartTimeUtc:', result.get('StartTimeUtc'))
    print('EndTimeUtc:', result.get('EndTimeUtc'))
    if result.get('ErrorMessage'):
        print('ErrorMessage:', result['ErrorMessage'])
    if result['Status'] != 'Completed':
        sys.exit(1)
