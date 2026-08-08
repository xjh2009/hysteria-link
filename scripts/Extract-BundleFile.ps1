param(
    [Parameter(Mandatory)]
    [string] $AppHost,
    [Parameter(Mandatory)]
    [string] $FileName,
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$AppHost = (Resolve-Path -LiteralPath $AppHost).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $AppHost) $FileName
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

# .NET apphost bundle signature. The preceding Int64 stores the bundle header offset.
$signature = [byte[]] (
    0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
    0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
    0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
    0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
)

$image = [IO.File]::ReadAllBytes($AppHost)
$signatureOffset = -1
for ($i = 8; $i -le $image.Length - $signature.Length; $i++) {
    if ($image[$i] -ne $signature[0]) { continue }
    $matched = $true
    for ($j = 1; $j -lt $signature.Length; $j++) {
        if ($image[$i + $j] -ne $signature[$j]) {
            $matched = $false
            break
        }
    }
    if ($matched) {
        $signatureOffset = $i
        break
    }
}

if ($signatureOffset -lt 8) {
    throw '输入文件不是受支持的 .NET 单文件 apphost。'
}

$headerOffset = [BitConverter]::ToInt64($image, $signatureOffset - 8)
$stream = [IO.File]::OpenRead($AppHost)
try {
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    $stream.Position = $headerOffset
    $major = $reader.ReadUInt32()
    $minor = $reader.ReadUInt32()
    $fileCount = $reader.ReadInt32()
    $null = $reader.ReadString() # Bundle ID

    if ($major -ge 2) {
        $null = $reader.ReadInt64()  # deps.json offset
        $null = $reader.ReadInt64()  # deps.json size
        $null = $reader.ReadInt64()  # runtimeconfig.json offset
        $null = $reader.ReadInt64()  # runtimeconfig.json size
        $null = $reader.ReadUInt64() # Bundle flags
    }

    $entry = $null
    for ($i = 0; $i -lt $fileCount; $i++) {
        $offset = $reader.ReadInt64()
        $size = $reader.ReadInt64()
        $compressedSize = if ($major -ge 6) { $reader.ReadInt64() } else { 0 }
        $null = $reader.ReadByte() # File type
        $relativePath = $reader.ReadString()

        if ($relativePath -ieq $FileName) {
            $entry = [pscustomobject]@{
                Offset = $offset
                Size = $size
                CompressedSize = $compressedSize
            }
            break
        }
    }

    if ($null -eq $entry) {
        throw "apphost Bundle 中没有找到 $FileName。"
    }

    $stream.Position = $entry.Offset
    $storedSize = if ($entry.CompressedSize -gt 0) {
        $entry.CompressedSize
    } else {
        $entry.Size
    }
    if ($storedSize -gt [int]::MaxValue) {
        throw "$FileName 过大，当前脚本无法提取。"
    }

    $data = $reader.ReadBytes([int] $storedSize)
    if ($data.Length -ne $storedSize) {
        throw "读取 $FileName 时意外到达 apphost 末尾。"
    }

    if ($entry.CompressedSize -gt 0) {
        $source = [IO.MemoryStream]::new($data, $false)
        $deflate = [IO.Compression.DeflateStream]::new(
            $source,
            [IO.Compression.CompressionMode]::Decompress
        )
        $target = [IO.File]::Create($OutputPath)
        try {
            $deflate.CopyTo($target)
        } finally {
            $target.Dispose()
            $deflate.Dispose()
            $source.Dispose()
        }
    } else {
        [IO.File]::WriteAllBytes($OutputPath, $data)
    }
} finally {
    $stream.Dispose()
}

$assembly = [Reflection.AssemblyName]::GetAssemblyName($OutputPath)
Write-Host "已提取: $OutputPath"
Write-Host "程序集: $($assembly.Name)"
