$root = Split-Path -Parent $PSScriptRoot
& "$root\\tjpdf.exe" objects @Args
