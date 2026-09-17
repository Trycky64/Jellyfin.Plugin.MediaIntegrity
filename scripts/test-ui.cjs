// Executes the served configuration-page JavaScript against a minimal DOM/API
// contract. This is not a visual browser test.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(process.argv[2] || 'Configuration/configPage.html', 'utf8');
const stats = process.argv[3] ? JSON.parse(fs.readFileSync(process.argv[3], 'utf8')) : {
    LastScanDate: '2026-09-15T16:38:26Z', Checked: 129, Healthy: 122,
    Warnings: 7, Queued: 0, Unreadable: 0, Corrupted: 0
};
const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(m => [m[1], {
    value: '', checked: false, textContent: '', listeners: {},
    addEventListener(name, fn) { this.listeners[name] = fn; }
}]));
const ids = [...html.matchAll(/id="([^"]+)"/g)].map(m => m[1]);
assert.equal(new Set(ids).size, ids.length, 'configuration IDs must be unique');
for (const id of ['MaxRepairsPerRun', 'StreamDurationToleranceSeconds', 'StreamStartTimeToleranceSeconds']) {
    assert.ok(elements.has(id), `missing ${id}`);
    assert.match(html, new RegExp(`for="${id}"[\\s\\S]*?id="${id}"`));
}
const config = {
    DryRun: true, MaxRepairsPerRun: 1, SourceRoot: '/media', RepairRoot: '/repair-media',
    BackupRoot: '/repair-backups', TempRoot: '/cache/media-integrity',
    EnableVideoFiles: true, EnableAudioFiles: true, ProbeTimeoutSeconds: 120,
    MaxParallelProbes: 1, MaxRepairAttempts: 3, RemuxTimeoutSeconds: 3600,
    ValidationTimeoutSeconds: 3600, DurationToleranceSeconds: 2,
    StreamDurationToleranceSeconds: 0.05, StreamStartTimeToleranceSeconds: 0.01,
    KeepBackups: false, ValidateFullPacketPass: true, PreserveOriginalContainer: true,
    AllowRepairOfCorrupted: false, EnableAudioVideoSyncCheck: true, EnablePacketTimelineAnalysis: false
};
let saved;
const context = {
    document: { querySelector: s => elements.get(s.slice(1)), getElementById: id => elements.get(id) },
    Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} },
    ApiClient: {
        getPluginConfiguration: async () => ({ ...config }),
        updatePluginConfiguration: async (_, value) => { saved = value; },
        getUrl: path => { assert.equal(path, 'MediaIntegrity/LastScan'); return path; },
        getJSON: async () => stats
    },
    console: { error: (...args) => { throw Error(args.join(' ')); } },
    window: { alert: msg => { throw Error(msg); } }
};
vm.runInNewContext(html.match(/<script[^>]*>([\s\S]*?)<\/script>/)[1], context);
(async () => {
    elements.get('MediaIntegrityConfigurationPage').listeners.pageshow();
    await new Promise(resolve => setImmediate(resolve));
    const text = elements.get('LastScanStats').textContent;
    for (const label of ['Checked: 129', 'Healthy: 122', 'Warnings: 7', 'Queued: 0', 'Unreadable: 0', 'Duration differences:', 'Incomplete timeline:']) {
        assert.ok(text.includes(label), text);
    }
    assert.ok(!text.includes('undefined'), text);
    if (stats.AudioVideoDiagnostics) {
        const details = elements.get('LastScanAvDiagnostics').textContent;
        assert.ok(details.includes('Audio/video duration difference'), details);
        assert.ok(details.includes('audio 2'), details);
        assert.ok(details.includes('1.429 s'), details);
        assert.ok(details.includes('duration ratio 0.998999'), details);
    }
    assert.equal(elements.get('DryRun').checked, true);
    assert.equal(elements.get('RepairRoot').value, '/repair-media');
    elements.get('MaxParallelProbes').value = '2';
    elements.get('MediaIntegrityConfigurationForm').listeners.submit({ preventDefault() {} });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(saved.MaxParallelProbes, 2);
    assert.equal(saved.KeepBackups, true);
    assert.equal(saved.DryRun, true);
    assert.equal(saved.MaxRepairsPerRun, 1);
    assert.equal(saved.StreamDurationToleranceSeconds, 0.05);
    assert.equal(saved.StreamStartTimeToleranceSeconds, 0.01);
    assert.equal(saved.EnablePacketTimelineAnalysis, false);
    assert.equal(saved.EnableAudioVideoSyncCheck, true);
    console.log('PASS: served page statistics, configuration loading and save handler; no JavaScript errors.');
    if (!process.argv[3]) {
        require('node:child_process').execFileSync(process.execPath,
            [__filename, process.argv[2] || 'Configuration/configPage.html', 'tests/ui-lastscan-v110.json'],
            { stdio: 'inherit' });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
