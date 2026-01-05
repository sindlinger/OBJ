# OBJ (objects/textops module)

This sub-repo isolates the **objects/textops** implementation used for despacho extraction.
It ships the **actual C# objects commands** (no bash/powershell wrappers) **and the core despacho
extraction engine** (TjpbDespachoExtractor).

## Core commands (objects/textops)
- Diff (two PDFs):
  `tjpdf.exe objects operators diff --inputs a.pdf,b.pdf --obj 6 --op Tj,TJ`
- Variaveis (self):
  `tjpdf.exe objects operators var --input file.pdf --obj 6 --self --anchors --doc tjpb_despacho`
- Fixos (self):
  `tjpdf.exe objects operators fixed --input file.pdf --obj 6 --self --doc tjpb_despacho`
- Extração por op_range (por campo):
  `tjpdf.exe objects extractfields ESPECIALIDADE --input file.pdf --json`

## Operators grouping (novo)
Use `objects operators` para agrupar os comandos de operadores:
- `objects operators text`      -> textoperators
- `objects operators var`       -> textopsvar
- `objects operators fixed`     -> textopsfixed
- `objects operators diff`      -> textopsdiff
- `objects operators anchors`   -> textopsvar + `--anchors`

## Configs (root repo)
- Rules: `../configs/textops_rules/`
- Anchors: `../configs/textops_anchors/`
- Field maps: `../ExtractFields/`

## Extraction core (now inside OBJ)
The full `FilterPDF.TjpbDespachoExtractor` pipeline now lives here:
- Commands: `src/TjpbDespachoExtractor/Commands/`
- Extraction engine: `src/TjpbDespachoExtractor/Extraction/`
- Models/Config/Reference/Utils: `src/TjpbDespachoExtractor/*`
