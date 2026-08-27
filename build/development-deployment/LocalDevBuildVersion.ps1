Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Get-OpenBladeNextLocalDevBuildVersion {
    [CmdletBinding()]
    param(
        [string]$StatePath = (Join-Path $env:LOCALAPPDATA (
            'OpenBlade\LocalDevDeployment.bundle-version')),
        [long]$UnixTimeMilliseconds = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    )

    if ($UnixTimeMilliseconds -le 0) {
        throw 'The local-development build clock must be positive.'
    }

    $fullStatePath = [IO.Path]::GetFullPath($StatePath)
    $stateDirectory = Split-Path -Parent $fullStatePath
    if ([string]::IsNullOrWhiteSpace($stateDirectory)) {
        throw 'The local-development build-version state requires a parent directory.'
    }

    [IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
    $stream = [IO.FileStream]::new(
        $fullStatePath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None,
        64,
        [IO.FileOptions]::WriteThrough)
    try {
        if ($stream.Length -gt 20) {
            throw 'The local-development build-version state is malformed.'
        }

        $previous = 0L
        if ($stream.Length -gt 0) {
            $bytes = [byte[]]::new([int]$stream.Length)
            $read = $stream.Read($bytes, 0, $bytes.Length)
            if ($read -ne $bytes.Length) {
                throw 'The local-development build-version state could not be read completely.'
            }

            $text = [Text.Encoding]::ASCII.GetString($bytes)
            if ($text -notmatch '^(?:0|[1-9][0-9]*)$' -or
                -not [long]::TryParse(
                    $text,
                    [Globalization.NumberStyles]::None,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$previous)) {
                throw 'The local-development build-version state is malformed.'
            }
        }

        if ($previous -eq [long]::MaxValue) {
            throw 'The local-development build-version state is exhausted.'
        }

        $next = [Math]::Max($UnixTimeMilliseconds, $previous + 1)
        $radix = 65535L
        $radixSquared = $radix * $radix
        $high = [long][Math]::Floor($next / [double]$radixSquared)
        $remainder = $next % $radixSquared
        $middle = [long][Math]::Floor($remainder / [double]$radix)
        $low = $remainder % $radix
        if ($high -ge $radix) {
            throw 'The reserved local-development build-version range is exhausted.'
        }

        $serialized = $next.ToString([Globalization.CultureInfo]::InvariantCulture)
        $serializedBytes = [Text.Encoding]::ASCII.GetBytes($serialized)
        $stream.Position = 0
        $stream.SetLength(0)
        $stream.Write($serializedBytes, 0, $serializedBytes.Length)
        $stream.Flush($true)
        return "65535.$high.$middle.$low"
    }
    finally {
        $stream.Dispose()
    }
}
