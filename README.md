# OBJ (objects/textops module)

This sub-repo isolates the objects/textops workflow used for despacho extraction.
It does not ship code on its own; it provides the commands and wrappers to run
objects-based extraction from the main tjpdf repo.

## Run (wrapper)
- Bash: `./objects.sh <subcmd> [args]`
- PowerShell: `./objects.ps1 <subcmd> [args]`

Both wrappers call `../tjpdf.exe objects ...`.

## Core commands (objects/textops)
- Diff (two PDFs):
  `./objects.sh textopsdiff --inputs a.pdf,b.pdf --obj 6 --op Tj,TJ`
- Variaveis (self):
  `./objects.sh textopsvar --input file.pdf --obj 6 --self --anchors --doc tjpb_despacho`
- Fixos (self):
  `./objects.sh textopsfixed --input file.pdf --obj 6 --self --doc tjpb_despacho`
- Extração por op_range (por campo):
  `./objects.sh extractfields ESPECIALIDADE --input file.pdf --json`

## Configs (root repo)
- Rules: `../configs/textops_rules/`
- Anchors: `../configs/textops_anchors/`
- Field maps: `../ExtractFields/`
