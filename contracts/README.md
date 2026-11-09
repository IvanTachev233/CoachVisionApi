# Vision contracts (draft)

- `openapi/vision-v1.yaml`: the public `/v1/` API and webhooks (OpenAPI 3.1).
- `internal/raw-output/v1.json`: what the worker writes per video (JSON Schema 2020-12), with an example in `examples/`. Internal to Vision.

Check the API description from this folder:

```bash
npx @redocly/cli@2.76.0 lint openapi/vision-v1.yaml --config redocly.yaml
```

"Vision" and the server host are placeholders until the product is named (ADR 0016).
