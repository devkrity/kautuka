param(
    [string]$SolutionPath = ".",
    [int]$MinLines = 300
)

Get-ChildItem -Path $SolutionPath -Recurse -Filter "*.cs" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notlike "*\obj\*" -and $_.FullName -notlike "*\bin\*" } |
    ForEach-Object {
        $lineCount = (Get-Content -Path $_.FullName -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
        [PSCustomObject]@{
            Lines    = $lineCount
            FullPath = $_.FullName
        }
    } |
    Where-Object { $_.Lines -gt $MinLines } |
    Sort-Object Lines -Descending |
    Format-Table -AutoSize

   