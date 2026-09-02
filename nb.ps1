<#
.SYNOPSIS
    Packs a repository's useful source files into the fewest practical Markdown
    sources for Gemini Notebook/NotebookLM.

.DESCRIPTION
    Each emitted Markdown file contains path headings and fenced source code.
    It defaults to a conservative 450,000-word / 180-MiB ceiling per source,
    so a repository becomes one file whenever it fits aand otherwise only the
    minimum number of files needed to remain comfortably below typical source
    limits. A file is never split across sources.

    When Git is available, the file list is obtained with `git ls-files` and
    therefore respects .gitignore. Without Git, the script walks the directory
    tree and applies the configured exclusions.

.EXAMPLE
    .\Export-RepoToNotebookLM.ps1 -RepositoryPath C:\src\my-app

.EXAMPLE
    .\Export-RepoToNotebookLM.ps1 -RepositoryPath . -OutputDirectory .\notebooklm -Force

.EXAMPLE
    # Make fewer, larger sources if your NotebookLM plan permits it.
    .\Export-RepoToNotebookLM.ps1 -RepositoryPath . -MaxWordsPerFile 490000 -MaxBytesPerFile 190MB
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryPath = '.',

    # Relative paths are interpreted relative to RepositoryPath.
    [string]$OutputDirectory = 'notebooklm-code',

    [ValidateRange(1, 5000000)]
    [int]$MaxWordsPerFile = 450000,

    [ValidateRange(1, [long]::MaxValue)]
    [long]$MaxBytesPerFile = 180MB,

    # Supplying this replaces the built-in list.
    [string[]]$IncludeExtensions = @(
        '.ps1', '.psm1', '.psd1', '.js', '.mjs', '.cjs', '.jsx', '.ts', '.tsx',
        '.py', '.rb', '.php', '.java', '.kt', '.kts', '.go', '.rs', '.cs', '.fs',
        '.fsx', '.vb', '.c', '.h', '.cc', '.cpp', '.cxx', '.hxx', '.hpp', '.swift',
        '.scala', '.sc', '.sh', '.bash', '.zsh', '.fish', '.sql', '.r', '.lua',
        '.pl', '.pm', '.ex', '.exs', '.erl', '.hrl', '.dart', '.vue', '.svelte',
        '.astro', '.html', '.htm', '.css', '.scss', '.sass', '.less', '.xml',
        '.gradle', '.groovy', '.tf', '.hcl', '.sol', '.proto', '.graphql', '.gql',
        '.ipynb', '.clj', '.cljs', '.hs', '.elm', '.zig', '.nim', '.v',
        '.json', '.jsonc', '.yaml', '.yml', '.toml', '.ini', '.cfg', '.conf',
        '.properties', '.editorconfig'
    ),

    [string[]]$ExcludeDirectories = @(
        '.git', '.svn', '.hg', 'node_modules', 'vendor', 'bower_components',
        'dist', 'build', 'coverage', 'out', 'bin', 'obj', '.next', '.nuxt',
        '.venv', 'venv', '__pycache__', '.tox', '.mypy_cache', '.pytest_cache',
        'target', '.gradle', '.idea', '.vs', '.vscode'
    ),

    # Includes minified bundles and lockfiles, which are excluded by default
    # because they add a lot of low-value NotebookLM context.
    [switch]$IncludeGenerated,

    # Allows overwriting prior *-codebase-*.md files in OutputDirectory.
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SpecialFileNames = @(
    'Dockerfile', 'Containerfile', 'Makefile', 'Rakefile', 'Gemfile',
    'Jenkinsfile', 'Procfile', 'CMakeLists.txt', 'BUILD', 'BUILD.bazel',
    'WORKSPACE', 'WORKSPACE.bazel', '.gitignore', '.gitattributes',
    '.dockerignore', '.npmrc', '.prettierrc', '.eslintrc', 'Vagrantfile'
)

$GeneratedFilePatterns = @(
    '*.min.js', '*.min.css', '*.map', 'package-lock.json', 'npm-shrinkwrap.json',
    'yarn.lock', 'pnpm-lock.yaml', 'composer.lock', 'Gemfile.lock', 'Cargo.lock',
    'poetry.lock', 'Pipfile.lock', 'go.sum'
)

$LanguageByExtension = @{
    '.ps1' = 'powershell'; '.psm1' = 'powershell'; '.psd1' = 'powershell'
    '.js' = 'javascript'; '.mjs' = 'javascript'; '.cjs' = 'javascript'; '.jsx' = 'jsx'
    '.ts' = 'typescript'; '.tsx' = 'tsx'; '.py' = 'python'; '.rb' = 'ruby'; '.php' = 'php'
    '.java' = 'java'; '.kt' = 'kotlin'; '.kts' = 'kotlin'; '.go' = 'go'; '.rs' = 'rust'
    '.cs' = 'csharp'; '.fs' = 'fsharp'; '.fsx' = 'fsharp'; '.vb' = 'vbnet'
    '.c' = 'c'; '.h' = 'c'; '.cc' = 'cpp'; '.cpp' = 'cpp'; '.cxx' = 'cpp'; '.hpp' = 'cpp'
    '.swift' = 'swift'; '.scala' = 'scala'; '.sc' = 'scala'; '.sh' = 'bash'; '.bash' = 'bash'
    '.zsh' = 'zsh'; '.fish' = 'fish'; '.sql' = 'sql'; '.r' = 'r'; '.lua' = 'lua'
    '.pl' = 'perl'; '.pm' = 'perl'; '.ex' = 'elixir'; '.exs' = 'elixir'; '.erl' = 'erlang'
    '.hrl' = 'erlang'; '.dart' = 'dart'; '.vue' = 'vue'; '.svelte' = 'svelte'; '.astro' = 'astro'
    '.html' = 'html'; '.htm' = 'html'; '.css' = 'css'; '.scss' = 'scss'; '.sass' = 'sass'
    '.less' = 'less'; '.xml' = 'xml'; '.gradle' = 'groovy'; '.groovy' = 'groovy'
    '.tf' = 'hcl'; '.hcl' = 'hcl'; '.sol' = 'solidity'; '.proto' = 'protobuf'
    '.graphql' = 'graphql'; '.gql' = 'graphql'; '.ipynb' = 'json'; '.clj' = 'clojure'
    '.cljs' = 'clojure'; '.hs' = 'haskell'; '.elm' = 'elm'; '.zig' = 'zig'; '.nim' = 'nim'
    '.v' = 'verilog'; '.json' = 'json'; '.jsonc' = 'jsonc'; '.yaml' = 'yaml'; '.yml' = 'yaml'
    '.toml' = 'toml'; '.ini' = 'ini'; '.cfg' = 'ini'; '.conf' = 'conf'; '.properties' = 'properties'
    '.editorconfig' = 'ini'
}

function Get-FullPath([string]$Path, [string]$BasePath) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path -Path $BasePath -ChildPath $Path))
}

function Test-IsWithin([string]$ChildPath, [string]$ParentPath) {
    $child = [IO.Path]::GetFullPath($ChildPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $parent = [IO.Path]::GetFullPath($ParentPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $parent + [IO.Path]::DirectorySeparatorChar
    return $child.Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or $child.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-GitFileList([string]$Root) {
    try {
        $null = Get-Command git -ErrorAction Stop
        $items = & git -C $Root ls-files --cached --others --exclude-standard 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return @($items | Where-Object { $_ })
    }
    catch {
        return $null
    }
}

function Test-ExcludedDirectory([string]$RelativePath) {
    $segments = $RelativePath -split '[\\/]'
    foreach ($segment in $segments[0..([Math]::Max(0, $segments.Count - 2))]) {
        if ($ExcludeDirectories -contains $segment) { return $true }
    }
    return $false
}

function Get-Fence([string]$Text) {
    $longest = 0
    foreach ($match in [regex]::Matches($Text, '`+')) {
        if ($match.Length -gt $longest) { $longest = $match.Length }
    }
    return ('`' * [Math]::Max(3, $longest + 1))
}

function Get-Language([IO.FileInfo]$File) {
    $extension = $File.Extension.ToLowerInvariant()
    if ($LanguageByExtension.ContainsKey($extension)) { return $LanguageByExtension[$extension] }
    switch ($File.Name) {
        'Dockerfile' { return 'dockerfile' }
        'Containerfile' { return 'dockerfile' }
        'Makefile' { return 'makefile' }
        'CMakeLists.txt' { return 'cmake' }
        'Gemfile' { return 'ruby' }
        'Rakefile' { return 'ruby' }
        default { return 'text' }
    }
}

function Get-TextOrNull([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $probe = New-Object byte[] 8192
        $read = $stream.Read($probe, 0, $probe.Length)
        for ($index = 0; $index -lt $read; $index++) {
            if ($probe[$index] -eq 0) { return $null }
        }
        $stream.Position = 0
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

$repositoryItem = Get-Item -LiteralPath $RepositoryPath
if (-not $repositoryItem.PSIsContainer) {
    throw "RepositoryPath must be a directory: $RepositoryPath"
}
$repositoryRoot = $repositoryItem.FullName
$outputRoot = Get-FullPath -Path $OutputDirectory -BasePath $repositoryRoot

$normalizedExtensions = @($IncludeExtensions | ForEach-Object {
    if ($_.StartsWith('.')) { $_.ToLowerInvariant() } else { ('.' + $_).ToLowerInvariant() }
})

if ((Test-Path -LiteralPath $outputRoot) -and -not $Force) {
    $existingBundles = @(Get-ChildItem -LiteralPath $outputRoot -File -Filter '*-codebase-*.md' -ErrorAction SilentlyContinue)
    if ($existingBundles.Count -gt 0) {
        throw "Output already contains codebase Markdown files. Use -Force to replace them: $outputRoot"
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if ($Force) {
    Get-ChildItem -LiteralPath $outputRoot -File -Filter '*-codebase-*.md' -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

$gitFiles = Get-GitFileList -Root $repositoryRoot
if ($null -ne $gitFiles) {
    $candidatePaths = $gitFiles | ForEach-Object { Join-Path -Path $repositoryRoot -ChildPath $_ }
    $fileDiscovery = 'Git-indexed and non-ignored files'
}
else {
    $candidatePaths = Get-ChildItem -LiteralPath $repositoryRoot -File -Recurse -Force |
        ForEach-Object { $_.FullName }
    $fileDiscovery = 'directory traversal (Git unavailable or not a repository)'
}

$files = New-Object System.Collections.Generic.List[IO.FileInfo]
foreach ($candidatePath in $candidatePaths) {
    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) { continue }
    $file = Get-Item -LiteralPath $candidatePath
    if (Test-IsWithin -ChildPath $file.FullName -ParentPath $outputRoot) { continue }

    $relativePath = $file.FullName.Substring($repositoryRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (Test-ExcludedDirectory -RelativePath $relativePath) { continue }

    $isSpecial = $SpecialFileNames -contains $file.Name
    $isIncludedExtension = $normalizedExtensions -contains $file.Extension.ToLowerInvariant()
    if (-not ($isSpecial -or $isIncludedExtension)) { continue }

    if (-not $IncludeGenerated) {
        $generated = $false
        foreach ($pattern in $GeneratedFilePatterns) {
            if ($file.Name -like $pattern) { $generated = $true; break }
        }
        if ($generated) { continue }
    }
    $files.Add($file)
}

$files = @($files | Sort-Object { $_.FullName.Substring($repositoryRoot.Length) })
if ($files.Count -eq 0) {
    throw 'No matching source files were found. Use -IncludeExtensions to provide a custom extension list.'
}

$repositoryName = Split-Path -Leaf $repositoryRoot
$safeRepositoryName = ($repositoryName -replace '[^A-Za-z0-9._-]', '-')
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$bundleNumber = 0
$bundlePaths = New-Object System.Collections.Generic.List[string]
$skippedBinary = New-Object System.Collections.Generic.List[string]
$includedFileCount = 0
$currentBuilder = $null
$currentWordCount = 0
$currentByteCount = 0L
$currentSectionCount = 0

function Start-Bundle {
    $script:bundleNumber++
    $script:currentBuilder = New-Object Text.StringBuilder
    $header = "# $repositoryName source code (part $($script:bundleNumber))`n`n" +
        "Repository snapshot assembled for NotebookLM. Files are shown with their repository-relative paths.`n`n"
    [void]$script:currentBuilder.Append($header)
    $script:currentWordCount = ([regex]::Matches($header, '\S+')).Count
    $script:currentByteCount = $utf8NoBom.GetByteCount($header)
    $script:currentSectionCount = 0
}

function Save-Bundle {
    if ($null -eq $script:currentBuilder) { return }
    $name = '{0}-codebase-{1:D3}.md' -f $safeRepositoryName, $script:bundleNumber
    $path = Join-Path -Path $outputRoot -ChildPath $name
    [IO.File]::WriteAllText($path, $script:currentBuilder.ToString(), $utf8NoBom)
    $script:bundlePaths.Add($path)
}

Start-Bundle
foreach ($file in $files) {
    $text = Get-TextOrNull -Path $file.FullName
    $relativePath = $file.FullName.Substring($repositoryRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -replace '\\', '/'
    if ($null -eq $text) {
        $skippedBinary.Add($relativePath)
        continue
    }

    $fence = Get-Fence -Text $text
    $language = Get-Language -File $file
    $section = "## ``$relativePath``" + "`n`n" + "$fence$language" + "`n" + $text + "`n$fence`n`n"
    $sectionWords = ([regex]::Matches($section, '\S+')).Count
    $sectionBytes = $utf8NoBom.GetByteCount($section)

    if ($currentBuilder.Length -gt 0 -and
        (($currentWordCount + $sectionWords -gt $MaxWordsPerFile) -or
         ($currentByteCount + $sectionBytes -gt $MaxBytesPerFile)) -and
        $currentSectionCount -gt 0) {
        Save-Bundle
        Start-Bundle
    }

    [void]$currentBuilder.Append($section)
    $currentWordCount += $sectionWords
    $currentByteCount += $sectionBytes
    $currentSectionCount++
    $includedFileCount++
}
Save-Bundle

if ($includedFileCount -eq 0) {
    throw 'All matching files appeared to be binary. No Markdown was written.'
}

Write-Host "Created $($bundlePaths.Count) NotebookLM Markdown source file(s) in: $outputRoot"
Write-Host "Included $includedFileCount source file(s) using $fileDiscovery."
if ($skippedBinary.Count -gt 0) {
    Write-Warning "Skipped $($skippedBinary.Count) binary-looking file(s)."
}
foreach ($bundlePath in $bundlePaths) {
    $sizeMiB = [Math]::Round(((Get-Item -LiteralPath $bundlePath).Length / 1MB), 2)
    Write-Host ("  {0} ({1} MiB)" -f (Split-Path -Leaf $bundlePath), $sizeMiB)
}
