# DocDetector (encapsulated)

Encapsulated, independent detectors inside `OBJ/DocDetector/`.
Each module lives in its own directory and can be executed alone.

## Modules

### 1) ContentsTitleDetector
Detects document titles by scanning `/Contents` objects and text-operator prefixes.

Path:
- `OBJ/DocDetector/ContentsTitleDetector/`

Run:
```
/mnt/c/git/tjpdf/.dotnet/dotnet run --project OBJ/DocDetector/ContentsTitleDetector/ContentsTitleDetectorRunner.csproj -- <pdf>
```

### 2) BookmarkDetectorCSharp
Reads bookmarks using iText outlines (C#).

Path:
- `OBJ/DocDetector/BookmarkDetectorCSharp/`

Run:
```
/mnt/c/git/tjpdf/.dotnet/dotnet run --project OBJ/DocDetector/BookmarkDetectorCSharp/BookmarkDetectorRunner.csproj -- <pdf>
```

### 3) BookmarkDetectorJava
Original Java sample (unchanged).

Path:
- `OBJ/DocDetector/BookmarkDetectorJava/FetchBookmarkTitles.java`

### 4) OutlineRawCheck
Raw `/Outlines` check from the PDF catalog (no title extraction).

Path:
- `OBJ/DocDetector/OutlineRawCheck/`

Run:
```
/mnt/c/git/tjpdf/.dotnet/dotnet run --project OBJ/DocDetector/OutlineRawCheck/OutlineRawCheckRunner.csproj -- <pdf>
```

### 5) DespachoContentsDetector
Deteccao de despacho por bookmarks + /Contents + fallback (encapsulado).

Path:
- `OBJ/DocDetector/DespachoContentsDetector/`

## Outputs
- `OBJ/DocDetector/outputs/`

Note: these modules are **not wired** to any other pipeline.
