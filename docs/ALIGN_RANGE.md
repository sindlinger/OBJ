# ALIGN_RANGE (front_head/back_tail)

Este documento descreve **como o alignrange cria** `front_head` e `back_tail`
por **intervalo de operadores** (Tj/TJ).

Arquivos principais:
- `OBJ/Align/ObjectsTextOpsDiff.cs`
- `OBJ/DocDetector/DespachoContentsDetector/DespachoContentsDetector.cs`

## 1) Descobrir a página do despacho
O alignrange **não chama o CLI**. Ele resolve a página internamente:

1. `DespachoContentsDetector.ResolveContentsPageForDoc(...)`
   - **Bookmarks**: primeiro bookmark com “despacho”.
   - **/Contents**: varre páginas e procura “despacho” no prefixo ou cabeçalho.
   - **Fallback**: página com maior `/Contents`.

Se `--page N` foi informado, usa essa página diretamente.

## 2) Escolher o stream do despacho na página
`DespachoContentsDetector.FindLargestContentsStreamByPage(...)`:
- avalia todos os streams de `/Contents` da página;
- marca se há **título** (prefixo com “despacho”) e **cabeçalho** (labels da ROI);
- escolhe o **stream do corpo**:
  - se houver “título”, tenta **descartar o menor**;
  - se houver cabeçalho, pega o maior com cabeçalho;
  - senão, pega o **maior stream**.

## 3) Extrair blocos por operadores (SelfBlock)
`ExtractSelfBlocks(...)`:
- tokeniza o content stream;
- considera apenas operadores de texto (Tj/TJ);
- mantém um **contador de text ops** (`textOpIndex`);
- quebra blocos quando há:
  - operadores de quebra de linha (`T*`, `'`, `"`), ou
  - mudança de posição (Td/Tm) detectada por `ShouldFlushForPosition(...)`.

Cada bloco possui:
- `StartOp` / `EndOp` (índices dos text ops)
- `Text` (texto concatenado)

## 4) Alinhamento por blocos (DMP)
`BuildBlockAlignments(...)`:
- normaliza texto para similaridade (lower + dígitos → `#`);
- calcula **similaridade** com DiffMatchPatch:
  - `textSim` (levenshtein) e `lenSim`
  - score = `textSim * 0.7 + lenSim * 0.3`
- resolve alinhamento global com DP (gap = -0.35).

## 5) Gerar op_range (front_head/back_tail)
`ApplyAlignmentToRanges(...)`:
- marca blocos **diferentes** (A/B) como variáveis;
- se não houver nenhum, usa fallback (primeiro→último bloco).

`ApplyBackoff(...)`:
- `startOp = firstStartOp - backoff` (default backoff=2, mínimo 1)
- `endOp = lastEndOp`

## 6) Recorte e ValueFull
`ExtractValueFull(...)`:
- extrai o texto completo com op indices;
- recorta pelo `op_range`;
- normaliza e colapsa espaços;
- salva em `ValueFull`.

## 7) Front vs Back
`ComputeAlignRanges(...)`:
- **front_head** = página do despacho;
- **back_tail** = **página seguinte** ao despacho;
- se não existir a página seguinte, loga aviso e retorna `back_tail` vazio.

## Saída do alignrange
`ObjectsAlignRange` imprime e grava:
```
front_head:
  pdf_a: <arquivoA>
  op_range_a: opX-opY
  value_full_a: "<texto>"
back_tail:
  pdf_b: <arquivoB>
  op_range_b: opX-opY
  value_full_b: "<texto>"
```

## Observações importantes
- Não há regex no PDF inteiro.
- O recorte sempre gera `ValueFull`.
- O detector de despacho **é externo** (DocDetector) e é usado pelo alignrange.
