You are producing a focused summary for a single changed file in a pull request review.

## Review rules
{{rules}}

## File
- Path: `{{path}}`
- Before blob: `{{before_oid}}`
- After blob: `{{after_oid}}`

## Additional instructions
{{adhoc_instructions}}

## Your task

Read the file's diff and enough surrounding context to understand it, using read-only tools only.

If you find anything worth flagging at a specific location (bugs, risks, missing tests, unclear intent — not purely stylistic nits), or you call out a specific line or code snippet in your summary, you **must** call `add_annotation` for each such location **before** you finish. Use this JSON shape:

```json
{
  "path": "{{path}}",
  "blobOid": "string - the blob oid the line numbers refer to (before or after)",
  "startLine": 1,
  "endLine": 1,
  "side": "Old|New",
  "severity": "Info|Suggestion|Warning|Risk",
  "body": "string - the annotation text for the human reviewer; prefer a short heading and bullets over a dense paragraph"
}
```

You may call `add_annotation` multiple times, once per location. Only annotate things that materially matter for review.

Then call the `submit_file_summary` tool **exactly once** with a JSON object shaped like this:

```json
{
  "path": "{{path}}",
  "purpose": "string - what this file does and why it changed (prefer bullets)",
  "interestingChanges": "string - the notable changes in this diff (prefer bullets)",
  "reviewFocus": "string - what a reviewer should pay closest attention to (prefer bullets)"
}
```

Do not modify any files. Do not run shell commands.
