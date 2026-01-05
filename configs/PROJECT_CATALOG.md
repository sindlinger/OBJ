# Project Catalog

Purpose: Central reference for project structure and module ownership. Any new file should be registered here.

## Modules (authoritative)
- cli: CLI (dispatcher only)
- core: src/TjpdfPipeline.Core (engine: commands, orchestrator, extractors, models, utils)
- orchestrator: src/TjpdfPipeline.Core/Orchestrator (orquestracao do run e estagios)
- reference: reference (datasets only: peritos/honorarios/laudos)
- configs: configs (rules, anchors, field strategies)
- submodule_regex: submodule/src/AnchorTemplateExtractor (regex/anchor generator)
- submodule_nlp: submodule/tools (NLP runner in Python)
- outputs: outputs (final artifacts; not code)

## Root tree (snapshot)
```
/mnt/c/git/tjpdf
├── .gitignore
├── C:gittjpdftmprequerimento.json
├── C:gittjpdftmprequerimento.log
├── CLI
│   ├── Program.cs
│   └── TjpdfPipeline.Cli.csproj
├── README.md
├── TjpdfPipeline.Cli
├── TjpdfPipeline.Cli.deps.json
├── TjpdfPipeline.Cli.dll
├── TjpdfPipeline.Cli.runtimeconfig.json
├── TjpdfPipeline.sln
├── configs
│   ├── PROJECT_CATALOG.md
│   ├── anchor_templates
│   │   ├── tjpb_despacho_back_tail_annotated.txt
│   │   ├── tjpb_despacho_back_tail_nlp_input.txt
│   │   ├── tjpb_despacho_front_head_annotated.txt
│   │   ├── tjpb_despacho_front_head_nlp_input.txt
│   │   ├── tjpb_despacho_head_annotated.txt
│   │   ├── tjpb_despacho_head_nlp_input.txt
│   │   ├── tjpb_despacho_tail_annotated.txt
│   │   └── tjpb_despacho_tail_nlp_input.txt
│   ├── config.yaml
│   ├── fields
│   │   ├── arbitrados.yml
│   │   ├── arbitrados_certidao.yml
│   │   ├── arbitrados_despacho.yml
│   │   ├── especialidade_especie.yml
│   │   ├── juizo_comarca.yml
│   │   ├── perito.yml
│   │   ├── processo_partes.yml
│   │   └── valores_fator.yml
│   ├── textops_rules
│   │   └── tjpb_despacho.yml
│   ├── textops_anchors
│   │   └── .gitkeep
│   └── registry.yaml
├── docs
│   ├── CHANGELOG.md
│   ├── documentem
│   │   ├── ABORDAGENS.md
│   │   ├── ARVORE_ATUAL.md
│   │   ├── CLI.md
│   │   ├── PLANO.md
│   │   └── README.md
│   └── legacy_docs
│       ├── CHANGELOG_PIPELINE_TJPB.md
│       ├── PDF_STRUCTURE.md
│       ├── PIPELINE_ITINERARY.md
│       ├── PIPELINE_TJPB_CORE.md
│       ├── PIPELINE_TJPB_FLOW.md
│       ├── PIPELINE_TJPB_STATUS.md
│       └── tjpb_json_observacao.json
├── reference
│   ├── laudos
│   │   ├── ESTUDO SOCIAL
│   │   │   ├── 016110_13_2025_8_15_SEI_016110_13.2025.8.15.zip
│   │   │   ├── 016144_21_2025_8_15_SEI_016144_21.2025.8.15.zip
│   │   │   ├── 016155_35_2025_8_15_SEI_016155_35.2025.8.15.zip
│   │   │   ├── 016158_30_2025_8_15_SEI_016158_30.2025.8.15.zip
│   │   │   └── 016178_29_2025_8_15_SEI_016178_29.2025.8.15.zip
│   │   ├── LAUDO DE AVALIAÇÃO DE IMÓVEL URBANO, CONFORME NORMAS DA ABNT RESPECTIVA
│   │   │   ├── 004517_64_2025_8_15_SEI_004517_64.2025.8.15.zip
│   │   │   ├── 015045_42_2025_8_15_SEI_015045_42.2025.8.15.zip
│   │   │   ├── 015611_04_2025_8_15_SEI_015611_04.2025.8.15.zip
│   │   │   ├── 016774_38_2025_8_15_SEI_016774_38.2025.8.15.zip
│   │   │   ├── 017806_64_2025_8_15_SEI_017806_64.2025.8.15.zip
│   │   │   ├── 018172_36_2025_8_15_SEI_018172_36.2025.8.15.zip
│   │   │   ├── 018356_85_2025_8_15_SEI_018356_85.2025.8.15.zip
│   │   │   ├── 018893_63_2025_8_15_SEI_018893_63.2025.8.15.zip
│   │   │   ├── 018894_29_2025_8_15_SEI_018894_29.2025.8.15.zip
│   │   │   ├── 018949_02_2025_8_15_SEI_018949_02.2025.8.15.zip
│   │   │   ├── 019206_91_2025_8_15_SEI_019206_91.2025.8.15.zip
│   │   │   ├── 019393_38_2025_8_15_SEI_019393_38.2025.8.15.zip
│   │   │   ├── 019406_81_2025_8_15_SEI_019406_81.2025.8.15.zip
│   │   │   ├── 019678_48_2025_8_15_SEI_019678_48.2025.8.15.zip
│   │   │   ├── 019888_86_2025_8_15_SEI_019888_86.2025.8.15.zip
│   │   │   ├── 020227_06_2025_8_15_SEI_020227_06.2025.8.15.zip
│   │   │   ├── 020615_06_2025_8_15_SEI_020615_06.2025.8.15.zip
│   │   │   ├── 020768_42_2025_8_15_SEI_020768_42.2025.8.15.zip
│   │   │   └── 020771_37_2025_8_15_SEI_020771_37.2025.8.15.zip
│   │   ├── LAUDO GRAFOTÉCNICO
│   │   │   ├── 007720_86_2024_8_15_SEI_007720_86.2024.8.15.zip
│   │   │   ├── 011157_23_2025_8_15_SEI_011157_23.2025.8.15.zip
│   │   │   ├── 016017_71_2025_8_15_SEI_016017_71.2025.8.15.zip
│   │   │   ├── 018077_65_2025_8_15_SEI_018077_65.2025.8.15.zip
│   │   │   ├── 018375_21_2025_8_15_SEI_018375_21.2025.8.15.zip
│   │   │   ├── 018763_21_2025_8_15_SEI_018763_21.2025.8.15.zip
│   │   │   ├── 019296_38_2025_8_15_SEI_019296_38.2025.8.15.zip
│   │   │   ├── 019525_12_2025_8_15_SEI_019525_12.2025.8.15.zip
│   │   │   ├── 019534_94_2025_8_15_SEI_019534_94.2025.8.15.zip
│   │   │   ├── 019830_21_2025_8_15_SEI_019830_21.2025.8.15.zip
│   │   │   ├── 020072_38_2025_8_15_SEI_020072_38.2025.8.15.zip
│   │   │   ├── 020074_67_2025_8_15_SEI_020074_67.2025.8.15.zip
│   │   │   ├── 020075_33_2025_8_15_SEI_020075_33.2025.8.15.zip
│   │   │   ├── 020076_96_2025_8_15_SEI_020076_96.2025.8.15.zip
│   │   │   ├── 020077_62_2025_8_15_SEI_020077_62.2025.8.15.zip
│   │   │   ├── 020560_33_2025_8_15_SEI_020560_33.2025.8.15.zip
│   │   │   ├── 020603_26_2025_8_15_SEI_020603_26.2025.8.15.zip
│   │   │   ├── 021306_83_2025_8_15_SEI_021306_83.2025.8.15.zip
│   │   │   ├── 021488_06_2025_8_15_SEI_021488_06.2025.8.15.zip
│   │   │   └── 021510_34_2025_8_15_SEI_021510_34.2025.8.15.zip
│   │   ├── LAUDO PSICOLÓGICO
│   │   │   ├── 015415_72_2025_8_15_SEI_015415_72.2025.8.15.zip
│   │   │   ├── 017649_67_2025_8_15_SEI_017649_67.2025.8.15.zip
│   │   │   ├── 017650_33_2025_8_15_SEI_017650_33.2025.8.15.zip
│   │   │   ├── 017651_96_2025_8_15_SEI_017651_96.2025.8.15.zip
│   │   │   ├── 017652_62_2025_8_15_SEI_017652_62.2025.8.15.zip
│   │   │   ├── 017653_28_2025_8_15_SEI_017653_28.2025.8.15.zip
│   │   │   ├── 017654_91_2025_8_15_SEI_017654_91.2025.8.15.zip
│   │   │   ├── 018248_71_2025_8_15_SEI_018248_71.2025.8.15.zip
│   │   │   ├── 018388_64_2025_8_15_SEI_018388_64.2025.8.15.zip
│   │   │   ├── 018394_54_2025_8_15_SEI_018394_54.2025.8.15.zip
│   │   │   ├── 018905_43_2025_8_15_SEI_018905_43.2025.8.15.zip
│   │   │   ├── 018983_10_2025_8_15_SEI_018983_10.2025.8.15.zip
│   │   │   ├── 019056_50_2025_8_15_SEI_019056_50.2025.8.15.zip
│   │   │   ├── 019218_71_2025_8_15_SEI_019218_71.2025.8.15.zip
│   │   │   ├── 019316_37_2025_8_15_SEI_019316_37.2025.8.15.zip
│   │   │   ├── 019328_17_2025_8_15_SEI_019328_17.2025.8.15.zip
│   │   │   ├── 019337_02_2025_8_15_SEI_019337_02.2025.8.15.zip
│   │   │   ├── 019341_60_2025_8_15_SEI_019341_60.2025.8.15.zip
│   │   │   ├── 019342_26_2025_8_15_SEI_019342_26.2025.8.15.zip
│   │   │   ├── 019346_84_2025_8_15_SEI_019346_84.2025.8.15.zip
│   │   │   ├── 019419_27_2025_8_15_SEI_019419_27.2025.8.15.zip
│   │   │   ├── 019423_85_2025_8_15_SEI_019423_85.2025.8.15.zip
│   │   │   ├── 019535_60_2025_8_15_SEI_019535_60.2025.8.15.zip
│   │   │   ├── 019540_84_2025_8_15_SEI_019540_84.2025.8.15.zip
│   │   │   ├── 019720_75_2025_8_15_SEI_019720_75.2025.8.15.zip
│   │   │   ├── 019723_70_2025_8_15_SEI_019723_70.2025.8.15.zip
│   │   │   ├── 019731_89_2025_8_15_SEI_019731_89.2025.8.15.zip
│   │   │   ├── 019824_31_2025_8_15_SEI_019824_31.2025.8.15.zip
│   │   │   ├── 019827_26_2025_8_15_SEI_019827_26.2025.8.15.zip
│   │   │   ├── 019912_46_2025_8_15_SEI_019912_46.2025.8.15.zip
│   │   │   ├── 019969_48_2025_8_15_SEI_019969_48.2025.8.15.zip
│   │   │   ├── 019970_14_2025_8_15_SEI_019970_14.2025.8.15.zip
│   │   │   ├── 020013_07_2025_8_15_SEI_020013_07.2025.8.15.zip
│   │   │   ├── 020859_52_2025_8_15_SEI_020859_52.2025.8.15.zip
│   │   │   ├── 021224_58_2025_8_15_SEI_021224_58.2025.8.15.zip
│   │   │   ├── 021377_94_2025_8_15_SEI_021377_94.2025.8.15.zip
│   │   │   ├── 021483_79_2025_8_15_SEI_021483_79.2025.8.15.zip
│   │   │   └── 022027_13_2025_8_15_SEI_022027_13.2025.8.15.zip
│   │   ├── SEM_ESPECIE
│   │   │   ├── 000219_17_2025_8_15_SEI_000219_17.2025.8.15.zip
│   │   │   ├── 003017_35_2024_8_15_SEI_003017_35.2024.8.15.zip
│   │   │   ├── 005071_46_2025_8_15_SEI_005071_46.2025.8.15.zip
│   │   │   ├── 006366_54_2025_8_15_SEI_006366_54.2025.8.15.zip
│   │   │   ├── 006722_75_2025_8_15_SEI_006722_75.2025.8.15.zip
│   │   │   ├── 008124_34_2025_8_15_SEI_008124_34.2025.8.15.zip
│   │   │   ├── 008242_89_2024_8_15_SEI_008242_89.2024.8.15.zip
│   │   │   ├── 009889_67_2025_8_15_SEI_009889_67.2025.8.15.zip
│   │   │   ├── 010489_37_2025_8_15_SEI_010489_37.2025.8.15.zip
│   │   │   ├── 010561_14_2025_8_15_SEI_010561_14.2025.8.15.zip
│   │   │   ├── 010707_94_2025_8_15_SEI_010707_94.2025.8.15.zip
│   │   │   ├── 010710_89_2025_8_15_SEI_010710_89.2025.8.15.zip
│   │   │   ├── 010712_21_2025_8_15_SEI_010712_21.2025.8.15.zip
│   │   │   ├── 013563_87_2025_8_15_SEI_013563_87.2025.8.15.zip
│   │   │   ├── 014216_98_2025_8_15_SEI_014216_98.2025.8.15.zip
│   │   │   ├── 015004_78_2025_8_15_SEI_015004_78.2025.8.15.zip
│   │   │   ├── 015425_23_2025_8_15_SEI_015425_23.2025.8.15.zip
│   │   │   ├── 015430_47_2025_8_15_SEI_015430_47.2025.8.15.zip
│   │   │   ├── 015911_86_2025_8_15_SEI_015911_86.2025.8.15.zip
│   │   │   ├── 015922_03_2025_8_15_SEI_015922_03.2025.8.15.zip
│   │   │   ├── 015925_95_2025_8_15_SEI_015925_95.2025.8.15.zip
│   │   │   ├── 015929_56_2025_8_15_SEI_015929_56.2025.8.15.zip
│   │   │   ├── 015935_46_2025_8_15_SEI_015935_46.2025.8.15.zip
│   │   │   ├── 017778_46_2025_8_15_SEI_017778_46.2025.8.15.zip
│   │   │   ├── 018142_86_2025_8_15_SEI_018142_86.2025.8.15.zip
│   │   │   ├── 018174_65_2025_8_15_SEI_018174_65.2025.8.15.zip
│   │   │   ├── 019523_80_2025_8_15_SEI_019523_80.2025.8.15.zip
│   │   │   ├── 019583_77_2025_8_15_SEI_019583_77.2025.8.15.zip
│   │   │   ├── 019608_03_2025_8_15_SEI_019608_03.2025.8.15.zip
│   │   │   ├── 019648_98_2025_8_15_SEI_019648_98.2025.8.15.zip
│   │   │   ├── 019677_82_2025_8_15_SEI_019677_82.2025.8.15.zip
│   │   │   ├── 019718_46_2025_8_15_SEI_019718_46.2025.8.15.zip
│   │   │   ├── 019753_20_2025_8_15_SEI_019753_20.2025.8.15.zip
│   │   │   ├── 020326_35_2025_8_15_SEI_020326_35.2025.8.15.zip
│   │   │   ├── 020916_54_2025_8_15_SEI_020916_54.2025.8.15.zip
│   │   │   ├── 021134_14_2025_8_15_SEI_021134_14.2025.8.15.zip
│   │   │   ├── 021186_89_2025_8_15_SEI_021186_89.2025.8.15.zip
│   │   │   ├── 021609_63_2025_8_15_SEI_021609_63.2025.8.15.zip
│   │   │   └── 021636_18_2025_8_15_SEI_021636_18.2025.8.15.zip
│   │   ├── laudos_por_especie.csv
│   │   ├── laudos_por_especie_classificado.csv
│   │   ├── laudos_por_especie_revisado.csv
│   │   ├── laudos_por_especie_scored.csv
│   │   ├── laudos_sem_especie_evidencias.csv
│   │   ├── laudos_sem_especie_evidencias.jsonl
│   │   ├── laudos_sem_especie_evidencias.tsv
│   │   ├── laudos_sem_especie_evidencias.xlsx
│   │   ├── laudos_sem_especie_evidencias_dedup.tsv
│   │   ├── laudos_sem_especie_evidencias_dedup.xlsx
│   │   └── laudos_sem_especie_evidencias_dedup_clean.tsv
│   ├── laudos_hashes
│   │   ├── laudos_hashes.csv
│   │   └── laudos_hashes_unique.csv
│   ├── peritos
│   │   ├── peritos_catalogo.csv
│   │   ├── peritos_catalogo_final.csv
│   │   ├── peritos_catalogo_parquet.csv
│   │   └── peritos_catalogo_relatorio.csv
│   └── valores
│       ├── honorarios_aliases.json
│       └── tabela_honorarios.csv
├── scripts
├── src
│   └── TjpdfPipeline.Core
│       ├── Approaches
│       │   ├── Diff
│       │   ├── Layout
│       │   ├── Scripts
│       │   ├── Segmentation
│       │   ├── Signature
│       │   └── YamlStrategies
│       ├── Commands
│       │   ├── Bookmarks
│       │   ├── Command.cs
│       │   ├── Extraction
│       │   ├── Forensic
│       │   ├── IngestGetCommand.cs
│       │   ├── IngestListCommand.cs
│       │   ├── IngestPdfCommand.cs
│       │   ├── IngestStructGetCommand.cs
│       │   ├── IngestStructListCommand.cs
│       │   ├── Input
│       │   ├── Inspect
│       │   ├── InspectCommand.cs
│       │   ├── Meta
│       │   ├── Nlp
│       │   ├── Support
│       │   ├── TjpbRunCommand.cs
│       │   ├── Raw
│       │   │   ├── RawPreprocessCommand.cs
│       │   │   └── RawAnalyzeCommand.cs
│       │   ├── Dto
│       │   │   └── DtoBuildCommand.cs
│       │   ├── Ext
│       │   │   └── ExtExportCommand.cs
│       │   └── TjpdfRunCommand.cs
│       ├── Configuration
│       │   └── FpdfConfig.cs
│       ├── Models
│       │   ├── DocumentSegmentationConfig.cs
│       │   ├── ModificationArea.cs
│       │   ├── PDFAnalysisModels.cs
│       │   ├── PageExtractionModels.cs
│       │   ├── PngConversionModels.cs
│       │   ├── TemplateModels.cs
│       │   └── TjpbPipelineDtos.cs
│       ├── Options
│       │   └── WordOption.cs
│       ├── Orchestrator
│       │   ├── Core
│       │   ├── Stages
│       │   └── TjpbRunner.cs
│       ├── PDFAValidationResult.cs
│       ├── PDFAnalyzer.cs
│       ├── TjpbDespachoExtractor
│       │   ├── Commands
│       │   ├── Config
│       │   ├── Extraction
│       │   ├── Models
│       │   ├── Reference
│       │   └── Utils
│       ├── TjpdfPipeline.Core.csproj
│       ├── Utils
│       │   ├── BookmarkClassifier.cs
│       │   ├── BookmarkExtractor.cs
│       │   ├── BrazilianCurrencyDetector.cs
│       │   ├── CacheManager.cs
│       │   ├── CacheMemoryManager.cs
│       │   ├── CustomJsonSerializer.cs
│       │   ├── DetailedImageExtractor.cs
│       │   ├── DocumentSegmenter.cs
│       │   ├── FormatManager.cs
│       │   ├── ImageDataExtractor.cs
│       │   ├── LanguageManager.cs
│       │   ├── LaudoHashDb.cs
│       │   ├── OutputManager.cs
│       │   ├── PageRangeParser.cs
│       │   ├── PageTypeDetector.cs
│       │   ├── PathUtils.cs
│       │   ├── PdfAccessManager7.cs
│       │   ├── PdfStructureExtractor.cs
│       │   ├── PgAnalysisLoader.cs
│       │   ├── PgDocStore.cs
│       │   ├── ProgressTracker.cs
│       │   ├── SqliteCacheStore.cs
│       │   └── StubDetectors.cs
│       ├── Version.cs
│       └── VersionManager.cs
├── submodule
│   ├── AnchorTemplateExtractor.sln
│   ├── README.md
│   ├── src
│   │   ├── AnchorTemplateExtractor
│   │   │   ├── AnchorExtractionEngine.cs
│   │   │   ├── AnchorTemplateExtractor.csproj
│   │   │   ├── CompileOptions.cs
│   │   │   ├── ExtractedField.cs
│   │   │   ├── ExtractionPlan.cs
│   │   │   ├── FieldDefinition.cs
│   │   │   ├── FieldPatterns.cs
│   │   │   ├── FieldType.cs
│   │   │   ├── FieldValidators.cs
│   │   │   ├── PdfTextService.cs
│   │   │   ├── TemplateAnnotatedParser.cs
│   │   │   ├── TemplateCompiler.cs
│   │   │   ├── TemplateSegment.cs
│   │   │   ├── TemplateTyped.cs
│   │   │   └── TextNormalizer.cs
│   │   └── AnchorTemplateExtractor.Demo
│   │       ├── AnchorTemplateExtractor.Demo.csproj
│   │       ├── DemoHelpers.cs
│   │       ├── Program.cs
│   │       └── SampleData.cs
│   ├── tests
│   │   └── AnchorTemplateExtractor.Tests
│   │       ├── AnchorTemplateExtractor.Tests.csproj
│   │       ├── AnchorTemplateExtractorTests.cs
│   │       └── Usings.cs
│   └── tools
│       └── nlp_probe_hf.py
├── tjpdf
└── tjpdf.exe

178 directories, 140 files
```

## src tree (snapshot)
```
/mnt/c/git/tjpdf/src
└── TjpdfPipeline.Core
    ├── Configuration
    ├── Models
    ├── Options
    ├── PDFAValidationResult.cs
    ├── PDFAnalyzer.cs
    ├── Strategies
    ├── TjpbDespachoExtractor
    ├── TjpdfPipeline.Core.csproj
    ├── Utils
    └── Version.cs
```

## Notes
- outputs/ is artifacts only; do not add code there.
- configs/ is the only place for extraction rules and templates.
- submodule_regex depends on submodule_nlp output (annotated text).
- Any new module must be added to configs/registry.yaml and this catalog.
