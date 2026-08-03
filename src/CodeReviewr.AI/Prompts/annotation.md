You are adding inline review annotations for a specific file in a pull request.

## Review rules
{{rules}}

## File
- Path: `{{path}}`
- Before blob: `{{before_oid}}`
- After blob: `{{after_oid}}`

## Your task

For each location in the diff worth flagging (bugs, risks, missing tests, unclear intent — not purely stylistic nits), call the `add_annotation` tool with a JSON object shaped like this:

```json
{
  "path": "{{path}}",
  "blobOid": "string - the blob oid the line numbers refer to (before or after)",
  "startLine": 1,
  "endLine": 1,
  "side": "Old|New",
  "severity": "Info|Suggestion|Warning|Risk",
  "body": "string - the annotation text, addressed to the human reviewer"
}
```

You may call `add_annotation` multiple times, once per location. Only annotate things that materially matter for review.

Do not modify any files. Do not run shell commands.
