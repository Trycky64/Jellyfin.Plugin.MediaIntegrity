# Security Policy

## Reporting Vulnerabilities

If you discover a security vulnerability in this plugin, please report it responsibly by opening a private security advisory on GitHub rather than a public issue. This gives maintainers time to address the issue before public disclosure.

## Sensitive Data

**Never publish or commit any of the following:**

- API keys, tokens, or passwords
- Jellyfin database files (`jellyfin.db` or any `.sqlite` / `.sqlite3` files)
- Jellyfin configuration data directories (`config/data/`)
- Server logs containing authentication tokens or API keys
- Private keys or certificates (`.key`, `.pem`, `.pfx`, `.p12`)
- `.env` files containing secrets

## API Key Handling

- Integration test scripts authenticate via the `JELLYFIN_API_KEY` environment variable.
- No secret should ever be stored in Git, including in scripts, configuration files, or documentation.
- If an API key is accidentally committed or exposed, **revoke it immediately** in Jellyfin Dashboard → API Keys and generate a new one.

## Plugin Security Model

- The plugin never accesses the Jellyfin database directly.
- Source library mounts should remain read-only (`/media`); modifications use only the writable repair mirror (`/repair-media`).
- Path traversal and symbolic link attacks are rejected by the path security service.
- The statistics API endpoint requires Jellyfin administrator authorization.
- Never edit the repair queue with untrusted paths or relax directory permissions while tasks are running.

## Best Practices

- Keep Jellyfin pinned to a tested version.
- Run the plugin with DryRun enabled by default.
- Review repair results before disabling DryRun.
- Maintain independent backups of valuable media.
- Monitor free disk space; the plugin does not automatically clean up backups.
