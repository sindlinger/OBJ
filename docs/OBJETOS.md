# Objetos — operadores de texto, variaveis/fixos e conhecimento externo

Este documento consolida **tudo o que foi feito** na linha de "objetos" (textops) e organiza os comandos.

## Objetivo
- Encontrar **o objeto que carrega os campos finais** e trabalhar no nivel do operador (`Tj/TJ`).
- Separar **fixos** (template) vs **variaveis** (campos).
- Transformar esse conhecimento em **regras externas** e **ancoras por objeto**, sem hardcode.

## FRONT-HEAD / BACK-TAIL (ROI) — DEFINICAO E USO
ATENCAO: FRONT-HEAD E BACK-TAIL SAO USADOS PARA DELIMITAR A ROI (REGIAO DE INTERESSE).

DEFINICAO (CONFORME A DOCUMENTACAO DO DTO):
- FRONT-HEAD = HEADER + SUBHEADER + PRIMEIRO PARAGRAFO DE CONTEUDO.
- BACK-TAIL = ULTIMO PARAGRAFO RELEVANTE + ASSINATURA/RODAPE (QUANDO EXISTIR).

POR QUE SERVEM:
- ELES DEFINEM O "RECORTE" DO DOCUMENTO ONDE O DESPACHO ESTA.
- A ROI PERMITE TRABALHAR COM OBJETOS/OPERADORES APENAS NO TRECHO RELEVANTE.

OBS: O FLUXO FINAL AINDA ESTA EM DEFINICAO E SERA REGISTRADO APOS VALIDACAO.

ARQUIVO DE REGRA (OP_RANGE POR OBJETO):
- `configs/textops_anchors/tjpb_despacho_obj6_roi.yml`
  - `front_head`: start_op/end_op por source_file (Processo -> Comarca).
  - `back_tail`: reservado (a preencher quando delimitarmos a cauda).

USO (diff com defaults):
```
tjpdf.exe objects operators diff --inputs a.pdf,b.pdf --obj 6 --op Tj,TJ --doc tjpb_despacho
```

## Comando unico (Objects) para localizar despacho
```
tjpdf-cli inspect objects despacho --input <pdf> [--page N]
tjpdf-cli inspect objects despacho --input-dir <dir> [--page N]
  [--out-dir <dir>]
  [--regex <pat>] [--range-start <pat>] [--range-end <pat>]
  [--backtail-start <pat>] [--backtail-end <pat>]
  [--doc <name>] [--no-roi]
```
Quando `--out-dir` é informado, salva um `.txt` por PDF usando o nome do arquivo (`<pdf>.txt`).
Se `--out-dir` não for informado, usa o diretório padrão `output/objects_despacho`.
Por padrão o comando usa **ROI** (`--doc tjpb_despacho`) para front_head/back_tail. Use `--no-roi` para forçar regex.
Este comando executa **objeto -> operador** automaticamente:
1) `inspect contents find` (localiza page+obj).
2) `inspect contents list` (lista streams do /Contents).
3) `inspect objects operators diff` (recorte por operadores Tj/TJ) na **pagina inicial**.
4) Repete o recorte na **pagina seguinte** (backtail).

## Regra de identificacao do despacho em PDF completo (objeto -> operador)
Objetivo: localizar o **stream correto** do `/Contents` e, em seguida, **recortar por operadores** (`Tj/TJ`).

### Regra (heuristica validada na quarentena)
1) **Localizar pagina/stream** que contém o despacho usando texto do stream (nao é o texto do PDF inteiro).
2) Se a pagina tiver **3 streams** no `/Contents`:
   - **menor (~len 10)**: geralmente vazio/banal (ignorar).
   - **maior**: quase sempre contém o **corpo do despacho**.
   - **stream médio/pequeno**: frequentemente contém a linha **"Despacho ... SEI ... / pg. N"**.
3) Confirmar o stream escolhido **no nivel de operador** (`Tj/TJ`) antes de recortar campos.

### Comando 1 — localizar stream (objeto)
Usa `inspect contents find` para retornar **page + obj** dos streams que contêm palavras‑chave:
```
tjpdf-cli inspect contents find --input <pdf> \
  --regex "(?i)diretoria\\s+especial|despacho|honor[aá]rios|per[ií]cia|pagamento|sei|processo\\s+nº" \
  --preview 80
```

### Comando 2 — listar /Contents da pagina escolhida
```
tjpdf-cli inspect contents list --input <pdf> --page <N>
```

### Comando 3 — recorte por operadores (Tj/TJ) no stream escolhido
Usa **Objects → Operators → diff** para recortar **por operadores**, nunca no documento inteiro:
```
tjpdf-cli inspect objects operators diff --inputs <pdf>,<pdf> --obj <OBJ_ID> --op Tj,TJ \
  --range-start "Diretoria\\s+Especial|Despacho" \
  --range-end "Comarca\\s+(?:da|de)\\s+[A-Za-zÀ-ú ]+\\." \
  --range-text
```

### Resultado esperado
- **OBJ_ID** aponta para o stream correto do despacho.
- O `range-text` retorna **apenas o recorte** do despacho (sem outras paginas/objetos).
- O recorte vira base para `extractfields` e para mapas YAML por campo.

## O que foi implementado (resumo detalhado)

### 1) Decode real dos bytes de Tj/TJ (por operador)
Antes: o texto dos operadores estava caindo em ISO-8859-1 e gerando lixo (`\0\b`), porque nao era feito o decode correto por fonte/ToUnicode.

Agora:
- O decode dos operadores usa o **byte string real do operador** (`TextRenderInfo.GetPdfString()`).
- Os bytes sao convertidos para Unicode com a **fonte ativa** (`PdfFont.Decode(...)`).
- Fallback para `GetText()` so quando necessario.
- Resultado: o texto impresso corresponde **exatamente** ao operador (`Tj/TJ`) — nao e texto agregado da pagina.

Arquivos envolvidos:
- `src/TjpdfPipeline.Core/Commands/Inspect/PdfTextExtraction.cs`
- `src/TjpdfPipeline.Core/Commands/Inspect/ObjectsTextOperators.cs`
- `OBJ/Align/ObjectsTextOpsDiff.cs`

### 2) Self mode (1 PDF) para variaveis/fixos
`inspect objects textopsvar` e `textopsfixed` agora aceitam **1 PDF** (modo self).

Heuristica self:
- Quebra em blocos por linha (trocando quando ha `Tm`, `Td`, `T*`, `'`, `"`).
- Monta um **pattern** por bloco (sequencia de tamanhos de tokens).
- Conta frequencia do pattern:
  - **variavel** = pattern aparece pouco (<= `self-pattern-max`) e tem token >= `self-min-len`.
  - **fixo** = o resto.

Flags self:
- `--self-min-len`: tamanho minimo de token no bloco (default 1).
- `--self-pattern-max`: frequencia maxima do pattern para classificar como variavel (default 1).

### 3) Diff mode (2 PDFs) continua existindo
`textopsdiff`/`textopsvar`/`textopsfixed` com `--inputs a.pdf,b.pdf`:
- compara **linha por linha** (operadores) e/ou **blocos de texto** alinhados.
- usa alinhamento por tokens (DiffMatchPatch) para achar diferencas.
- `--ops-diff`: compara **operadores/bytes** (sem ToUnicode) ao inves do texto decodificado.

### 4) Blocos e range
Para diff e self, adicionados:
- `--blocks`: agrega linhas variaveis em blocos contiguos.
- `--blocks-inline`: imprime bloco em linha unica.
- `--blocks-order block-first|text-first`: escolhe ordem de impressao.
- `--blocks-range 15-20`: imprime apenas bloco(s) desejado(s).

### 5) Ancora (prev/next fixo)
`--anchors` (self):
- imprime para cada bloco variavel o **fixo anterior** e **fixo posterior**.

### 6) Regras externas (conhecimento fora do codigo)
Criado um **arquivo de regras** para classificar fixo/variavel no self:
- Diretorio: `configs/textops_rules/`
- Exemplo: `configs/textops_rules/tjpb_despacho.yml`
- Opcoes:
  - `--rules <arquivo.yml>` (caminho direto)
  - `--doc <nome>` (procura em `configs/textops_rules/<nome>.yml`)

Estrutura (resumo):
```
version: 1
doc: tjpb_despacho
self:
  min_token_len: 1
  pattern_max: 1
  fixed:
    - regex: '^(?i)refer(?:ência|encia)\s*:?$'
  variable:
    - regex: '\b\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}\b'   # CNJ
    - regex: '\b(?:SEI\s*)?(?:\d{6,7}-\d{2}\.\d{4}\.\d\.\d{2}(?:\.\d{4})?|\d{6,}\.\d{6}/\d{4}-\d{2})\b'
    - regex: '\b\d{3}\.\d{3}\.\d{3}-\d{2}\b'
    - regex: '\b\d{1,2}\s+de\s+[A-Za-z]+\s+de\s+\d{4}\b'
    - regex: '(?:R\$\s*)?\d{1,3}(?:\.\d{3})*,\d{2}'
```

Regra de precedencia:
- Primeiro aplica heuristica self.
- Depois **fixo** forca bloco a ser fixo.
- Depois **variavel** forca bloco a ser variavel (sobrescreve fixo se ambas casarem).

### 7) Anchors por objeto (salvos em arquivo)
Criado diretorio para salvar anchors por objeto:
- `configs/textops_anchors/`

Nova flag:
- `--anchors-out <dir|file>` (somente com `--anchors`)

Exemplo:
```
./tjpdf.exe inspect objects textopsvar --input <pdf> --obj 20 --op Tj,TJ --anchors --doc tjpb_despacho --anchors-out configs/textops_anchors/
```

Formato YAML salvo:
```
version: 1
doc: tjpb_despacho
source_file: 0012999__p3.pdf
obj: 20
anchors:
  - var_index: 1
    block: 2
    start_op: 12
    end_op: 43
    var: " Processo nº 001897-91.2024.8.15"
    prev_block: 1
    prev: "Referência:"
    next_block: null
    next: ""
```

### 8) Anchors conceituais (um arquivo para o despacho)
Quando a ideia e **conhecimento do documento** (paradigma), use `--anchors-merge`:

```
./tjpdf.exe inspect objects textopsvar --inputs a.pdf,b.pdf,c.pdf --obj 6 --op Tj,TJ --anchors --doc tjpb_despacho --anchors-merge --anchors-out configs/textops_anchors/
```

Isso gera **um arquivo** conceitual:
- `configs/textops_anchors/tjpb_despacho_obj6_concept.yml`

Estrutura (resumo):
```
version: 1
doc: tjpb_despacho
obj: 6
sources:
  - 0012999__p1.pdf
  - 0013996__p1.pdf
anchors:
  - prev: "Diretoria Especial - Tribunal de Justiça"
    next: "reais e ... arbitrados em favor ..."
    count: 7
    examples:
      - var: "Despacho nº 0012999/2024/DIESP"
        source_file: 0012999__p1.pdf
        start_op: 87
        end_op: 116
```

### 9) Mapa preliminar de fields por operador (esboco)
Para manter o conhecimento **fora do codigo**, criamos um arquivo de **mapeamento preliminar**
que liga **campo final -> candidatos com op_range real** no objeto 6.

Arquivo:
- `configs/textops_fields/tjpb_despacho_obj6_fields.draft.yml`

Esse arquivo **nao extrai ainda** o campo; ele apenas aponta onde estao os candidatos
por ancora (prev/next) e registra o **op_range real** (start/end + operador) do PDF,
incluindo **prev_op_range** e **next_op_range** quando possivel.

Exemplo:
```
fields:
  PROCESSO_JUDICIAL:
    candidates:
      - source_file: 0012999__p1.pdf
        op_range: op119-149[Tj]
        prev_op_range: [op47-86[Tj]]
        next_op_range: [op459-559[Tj]]
        # opcional: quando o campo está em outro /Contents (ex.: P2/P3)
        # obj: 14
        prev: "Diretoria Especial - Tribunal de Justiça"
        next: "reais e sessenta e três centavos)..."
        hint: "var contem 'Processo nº ...'"
```

Objetivo:
- validar em mais PDFs
- depois promover para um mapa definitivo (com extracao real)

### 10) Extracao real por op_range (blindada)
Ideia: o YAML vira um **template de extracao**. Nao usa heuristica nem conferencias por texto.
Ele serve apenas para apontar **op_range real** (operadores Tj/TJ) e extrair o texto por bytes.

**Passo a passo (proposto):**
1) Abrir o PDF e localizar o **obj** (ex.: 6).
2) Parsear o content stream e **contar Tj/TJ** na ordem real do stream.
3) Para cada **op_range** do YAML, coletar os bytes do operador (PdfString / PdfArray).
4) Decodificar **somente** com fonte ativa + ToUnicode (sem fallback de texto).
5) Concatenar o resultado do range e devolver o valor do campo.

**Validação com arquivos reais (sem heuristica):**
- Confirmar se o **op_range existe** no PDF (e nao o texto em si).
- Usar `inspect objects textopsvar/textopsfixed --self --blocks-inline` para ver as faixas `opXXX-YYY[Tj]`.
- Conferir somente **posicao/intervalo**, nao conteudo.

**Exemplo de template (real):**
```
fields:
  PROCESSO_JUDICIAL:
    candidates:
      - source_file: 0012999__p1.pdf
        op_range: op119-149[Tj]
        prev_op_range: [op47-86[Tj]]
        next_op_range: [op459-559[Tj]]
```

## Mapa dos comandos de objetos (organizado)

### 1) Inventario de objetos
- `inspect objects list --input file.pdf [--limit N]`
- `inspect objects analyze --input file.pdf [--limit N]`
- `inspect objects deep --input file.pdf [--limit N]`
- `inspect objects table --input file.pdf [--limit N]`
- `inspect objects filter /XObject /Image --input file.pdf`

### 2) Texto por objeto
- `inspect objects text --input file.pdf`
- `inspect objects textoperators --input file.pdf [--id N] [--limit N]`

### 3) Variaveis/fixos por objeto
Diff (2 PDFs):
- `inspect objects textopsvar   --inputs a.pdf,b.pdf --obj N [--blocks] [--blocks-inline] [--blocks-order block-first|text-first] [--blocks-range 15-20] [--ops-diff]`
- `inspect objects textopsfixed --inputs a.pdf,b.pdf --obj N [--ops-diff]`
- `inspect objects textopsdiff  --inputs a.pdf,b.pdf --obj N [--ops-diff]`

Self (1 PDF):
- `inspect objects textopsvar   --input file.pdf --obj N --self [--blocks-inline] [--self-min-len 2] [--self-pattern-max 1] [--blocks-range 15-20] [--anchors] [--anchors-out <dir|file>] [--rules <yml> | --doc <nome>]`
- `inspect objects textopsfixed --input file.pdf --obj N --self [--rules <yml> | --doc <nome>]`

### 4) Extracao por op_range (template)
- `inspect objects extractfields --input file.pdf --map <map.yml> [--fields a,b] [--validate] [--json] [--out <arquivo>]`

### 5) Shell interativo (inspecao por objeto)
- `inspect objects shell --input file.pdf`
  - `ls` lista objetos com nome (trecho do stream)
  - `cd <id>` entra no objeto
  - Dentro: `text`, `operators`, `textoperators`, `dump`, `dumphex`

**Nota:** `operators` = todos os operadores do stream (texto + graficos).  
`textoperators` = apenas operadores de texto (Tj/TJ/BT/ET/Tf/Tm/Td etc.).

## Exemplos praticos (despacho)

### Regra empirica (despacho)
- **Objeto 6**: carrega o texto principal do despacho (campos).
- **Objeto 7**: linha de titulo/rodape (coincide com o bookmark da pagina).

### P1 (obj 6)
Arquivos salvos:
- `outputs/inspect/textopsvar_obj6_tj.txt`
- `outputs/inspect/textopsfixed_obj6_tj.txt`

### P2 (obj 14)
Arquivos salvos:
- `outputs/inspect/self_blocks_0012999__p2.txt`
- `outputs/inspect/fixed_blocks_0012999__p2.txt`
- `outputs/inspect/anchors_0012999__p2.txt`

### P3 (obj 20 + 21)
P3 tem mais de um `/Contents` (obj 20 e 21).  
Rodar ambos para capturar tudo.

Arquivos salvos:
- `outputs/inspect/fixed_blocks_0012999__p3_obj20.txt`
- `outputs/inspect/anchors_0012999__p3_obj20.txt`
- `outputs/inspect/fixed_blocks_0012999__p3_obj21.txt`
- `outputs/inspect/anchors_0012999__p3_obj21.txt`

Com regras (`--doc tjpb_despacho`), obj 20 passa a ter:
- fixo: `Referência:`
- variaveis: `Processo nº ...` e `SEI nº ...` (com ancora no fixo).

## Observacoes importantes
- **Saida CP850**: o EXE no Windows imprime em CP850.  
  Para ler no WSL:
  - `iconv -f CP850 -t UTF-8 <arquivo>`
- **Zero fixos no self**: quando o texto e curto e nao repete padrao, o self pode nao marcar fixos.  
  Use `--doc` (regras) ou diff (2 PDFs).
- **Multiplo /Contents**: paginas podem ter mais de um objeto de texto.  
  Use `qpdf --show-pages` ou `inspect objects list` para achar os IDs.

## Onde guardar conhecimento
- Regras (fixo/variavel): `configs/textops_rules/`
- Anchors por objeto: `configs/textops_anchors/`
