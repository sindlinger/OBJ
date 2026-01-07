# DespachoContentsDetector

Detector encapsulado para localizar **página/stream do despacho** por:
1) **Bookmarks** (titulo contendo “despacho”)
2) **/Contents** (cabeçalho nos 3 streams e labels da ROI)
3) **Fallback**: maior /Contents (quando permitido)

## Principais APIs
- `GetDespachoHeaderLabels(rulesDoc, objId)`
- `ResolveContentsPageForDoc(...)`
- `FindLargestContentsStreamByPage(...)`
- `ResolveRoiPathForObj(rulesDoc, objId)`

## Observações
- Não roda regex no PDF inteiro.
- Serve como “abordagem paralela” ao detector por bookmarks puro.
