# Align (objects/textops engine)

Este diretório encapsula a lógica de **diff/align por operadores** (Tj/TJ) e a geração de
**front-head/back-tail (op_range)**. O CLI apenas chama estas rotinas.

## Entry points
- `ObjectsTextOpsDiff.Execute(...)`  
  - Modos: `Fixed`, `Variations`, `Both`, `Align`
- `ObjectsTextOpsDiff.ComputeAlignRanges(...)`  
  - Retorna `AlignRangeResult` com `front_head` e `back_tail`

## Arquivos (parciais)
- `ObjectsTextOpsDiff.cs` — entrypoint + fluxo principal
- `ObjectsTextOpsDiff.AlignRange.cs` — alinhamento e geração de op_range
- `ObjectsTextOpsDiff.SelfBlocks.cs` — extração/classificação de blocos
- `ObjectsTextOpsDiff.Helpers.cs` — tokenização, range, utilidades

## Recursos principais
- DiffMatchPatch por tokens para alinhar blocos variáveis
- Self mode (1 PDF) para fixos/variáveis
- Emissão de `ValueFull` no recorte (alignrange)

## Deteccao de despacho (fora do Align)
- `Obj.DocDetector.DespachoContentsDetector` concentra bookmark + /Contents + fallback.

## Consumo pelo CLI
- `tjpdf.exe objects operators diff|var|fixed|align`
- `tjpdf-cli inspect objects alignrange`
