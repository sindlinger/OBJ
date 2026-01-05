# Fields finais (objetivo do programa)

Esta lista **nao faz parte do DTO base**. Ela e o objetivo final do TJPDF.
Os fields sao calculados **a partir do DTO base** + abordagens (YAML, NLP, regex, diff, etc.).

## Campos finais (objetivo unico)
1) PROCESSO_ADMINISTRATIVO
2) PROCESSO_JUDICIAL
3) COMARCA
4) VARA
5) PROMOVENTE
6) PROMOVIDO
7) PERITO
8) CPF_PERITO
9) ESPECIALIDADE
10) ESPECIE_DA_PERICIA
11) VALOR_ARBITRADO_JZ
12) VALOR_ARBITRADO_DE
13) VALOR_ARBITRADO_CM
14) VALOR_ARBITRADO_FINAL
15) DATA_ARBITRADO_FINAL
16) DATA_REQUISICAO
17) ADIANTAMENTO
18) PERCENTUAL
19) PARCELA
20) FATOR

## Observacao (Certidao CM)
- ADIANTAMENTO, PERCENTUAL, PARCELA e FATOR sao campos da certidao CM.

## Regra de VALOR_ARBITRADO_FINAL / DATA_ARBITRADO_FINAL
- Se houver VALOR_ARBITRADO_CM, ele e o final e a data e a decisao do Conselho (certidao CM).
- Se nao houver CM, usar VALOR_ARBITRADO_DE e a data do despacho.
- Se nao houver DE nem CM, usar VALOR_ARBITRADO_JZ e a data do despacho/requerimento.

## Observacao
- DATA_REQUISICAO vem do requerimento de pagamento de honorarios.
