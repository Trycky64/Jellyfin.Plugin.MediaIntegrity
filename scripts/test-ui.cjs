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
const config = {
    DryRun: true, MaxRepairsPerRun: 1, SourceRoot: '/media', RepairRoot: '/repair-media',
    BackupRoot: '/repair-backups', TempRoot: '/cache/media-integrity',
    EnableVideoFiles: true, EnableAudioFiles: true, ProbeTimeoutSeconds: 120,
    MaxParallelProbes: 1, MaxRepairAttempts: 3, RemuxTimeoutSeconds: 3600,
    ValidationTimeoutSeconds: 3600, DurationToleranceSeconds: 2,
    KeepBackups: false, ValidateFullPacketPass: true, PreserveOriginalContainer: true,
    AllowRepairOfCorrupted: false
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
    for (const label of ['Checked: 129', 'Healthy: 122', 'Warnings: 7', 'Queued: 0', 'Unreadable: 0']) {
        assert.ok(text.includes(label), text);
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
    console.log('PASS: served page statistics, configuration loading and save handler; no JavaScript errors.');
})().catch(error => { console.error(error); process.exitCode = 1; });
