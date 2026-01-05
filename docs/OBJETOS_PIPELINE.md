# Pipeline de Objetos (Operadores) — Despacho

Este documento descreve **como encontrar os filtros finais** (campos finais) **a partir dos operadores de objetos** e **extrair os valores via op_range**, partindo do pressuposto de que os arquivos **já são despachos encontrados e validados**.

## Objetivo
Extrair os campos finais do despacho usando **somente**:
- Operadores de texto `Tj/TJ` (textops)
- Âncoras fixas (prev/next)
- `op_range` por campo
- Regex **apenas no recorte**

Sem heurísticas de classificação de documento. Aqui o despacho **já foi identificado**.

## Campos finais (alvo)
Base oficial: `docs/documentem/FIELDS.md`
- PROCESSO_ADMINISTRATIVO
- PROCESSO_JUDICIAL
- COMARCA
- VARA
- PROMOVENTE / PROMOVIDO
- PERITO / CPF_PERITO
- ESPECIALIDADE / ESPECIE_DA_PERICIA
- VALOR_ARBITRADO_JZ / VALOR_ARBITRADO_DE / VALOR_ARBITRADO_CM
- DATA_DESPESA (DATA_ARBITRADO_FINAL)

## Entradas
- PDF já validado como **despacho**
- Objeto(s) do despacho (ex.: `obj 6` p1, `obj 14` p2)
- Regras externas (opcional): `configs/textops_rules/*.yml`
- Mapas YAML por campo: `ExtractFields/*.yml`

## Saída
JSON por campo com:
- `ValueFull` (texto completo do recorte)
- `Value` (valor parseado do campo)
- `OpRange`, `PrevFixed`, `NextFixed`

## Comandos (somente via `objects operators`)
> Os comandos antigos `textops*` foram removidos. Use **operators**.

### 1) Encontrar objeto(s) do despacho
Lista e identifica quais objetos carregam texto útil:
```
tjpdf.exe objects list --input file.pdf --detail analyze
```

Opcional (ROI em banda superior/inferior da página):
```
tjpdf.exe objects fronthead --input file.pdf
tjpdf.exe objects backtail --input file.pdf
```

### 2) Variáveis (self/diff) + Âncoras
Gera blocos variáveis e âncoras (prev/next):
```
tjpdf.exe objects operators var --input file.pdf --obj 6 --self --anchors --anchors-out configs/textops_anchors/
```

Para consolidar âncoras de vários PDFs (merge):
```
tjpdf.exe objects operators var --inputs a.pdf,b.pdf --obj 6 --anchors --anchors-merge --anchors-out configs/textops_anchors/
```

### 3) Fixos (self/diff)
Identifica blocos fixos do template:
```
tjpdf.exe objects operators fixed --input file.pdf --obj 6 --self --doc tjpb_despacho
```

### 4) Diff (quando há 2 PDFs)
Compara operadores para separar fixo/variável:
```
tjpdf.exe objects operators diff --inputs a.pdf,b.pdf --obj 6
```

### 5) Mapas YAML por campo (op_range)
Preencher os mapas em `ExtractFields/*.yml` com:
- `obj`
- `op_range` (ex.: `op119-149[Tj]`)
- `prev/next` (se houver)

### 6) Extração final por campo
Extrai o campo diretamente do `op_range`:
```
tjpdf.exe objects extractfields ESPECIALIDADE --input file.pdf --json
tjpdf.exe objects extractfields ESPECIE_DA_PERICIA --input file.pdf --json
```

## Regras de validação (criticas)
- **Regex somente no recorte (`ValueFull`)**
- `ValueFull` sempre presente no JSON, mesmo quando `Value` falha
- Não misturar despacho com certidão/requerimento
- Manter **nomes completos** de campos

## Observações finais
- Esse pipeline assume o **despacho já identificado** (arquivo válido).
- A fase de identificação do despacho em PDFs completos é **separada**.
- A extração final **nunca** deve usar texto “achatado” do documento inteiro.

