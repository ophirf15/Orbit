# Hermes local Docker (optional)

Machine-local reference only. Do not require this for `.\build.ps1 -Test`.

```yaml
services:
  hermes:
    image: nousresearch/hermes-agent:latest
    restart: unless-stopped
    ports:
      - "8642:8642"
    volumes:
      - ./hermes-data:/opt/data
    environment:
      API_SERVER_ENABLED: "true"
      API_SERVER_HOST: "0.0.0.0"
      API_SERVER_KEY: "<strong secret>"
```

Orbit defaults: `http://127.0.0.1:8642`. Paste the same key into Settings → Hermes API key (sidecar). Use **Test Connection** to verify `/health` and `/v1/capabilities`.

Inspect current upstream docs before copying env vars blindly:

- https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/docker.md
- https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/api-server.md
