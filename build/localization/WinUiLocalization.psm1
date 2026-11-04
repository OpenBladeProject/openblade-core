Set-StrictMode -Version 3.0

function ConvertTo-WinUiResourceProperty {
    param([Parameter(Mandatory)][string]$Property)
    switch -CaseSensitive ($Property) {
        'AutomationProperties.Name' { '[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name' }
        'AutomationProperties.HelpText' { '[using:Microsoft.UI.Xaml.Automation]AutomationProperties.HelpText' }
        'ToolTipService.ToolTip' { '[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip' }
        default { $Property }
    }
}

function Get-WinUiLocalizationTags {
    param([Parameter(Mandatory)][string]$Text, [string]$Description = 'WinUI XAML')

    # Validate XML first; retain source offsets rather than reserializing XAML.
    try { $document = [xml]$Text }
    catch { throw "$Description is malformed XAML: $($_.Exception.Message)" }
    $elements = $document.SelectNodes('//*')
    $elementIndex = 0
    $localizableProperties = @('Text', 'Header', 'Content', 'PlaceholderText', 'Title',
        'AccessibleName', 'AccessKey', 'AutomationProperties.Name',
        'AutomationProperties.HelpText', 'ToolTipService.ToolTip')
    # Comments, processing instructions and CDATA must never become resource uses.
    $tagPattern = '(?s)<!--.*?-->|<\?.*?\?>|<!\[CDATA\[.*?\]\]>|<(?<tag>[A-Za-z][\w:.]*)(?<attributes>(?:[^>"'']|"[^"]*"|''[^'']*'')*)>'
    $attributePattern = '(?<property>[A-Za-z_][\w.:-]*)\s*=\s*(?:"(?<value>[^"]*)"|''(?<value>[^'']*)'')'
    foreach ($tag in [regex]::Matches($Text, $tagPattern)) {
        if (-not $tag.Groups['tag'].Success) { continue }
        $element = $elements[$elementIndex++]
        if ($element.get_Name() -cne $tag.Groups['tag'].Value) {
            throw "$Description has unsupported XAML tokenization."
        }
        $attributes = @([regex]::Matches($tag.Groups['attributes'].Value, $attributePattern))
        $uid = $null
        $name = $null
        $uidAttribute = $null
        $localized = [Collections.Generic.List[object]]::new()
        $prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($attribute in $attributes) {
            $property = $attribute.Groups['property'].Value
            # Let XML own entity decoding and attribute whitespace normalization;
            # regex offsets are used only for format-preserving source edits.
            $value = $element.GetAttribute($property)
            if ($property -ceq 'x:Uid') { $uid = $value; $uidAttribute = $attribute; continue }
            if ($property -ceq 'x:Name') { $name = $value; continue }
            $isCustom = $value -match '^\{\s*CustomResource\b'
            if ($property -cnotin $localizableProperties) {
                if ($isCustom) { throw "$Description uses CustomResource on unsupported property '$property'." }
                continue
            }
            $resourceProperty = ConvertTo-WinUiResourceProperty $property
            $key = $null
            $prefix = $null
            if ($isCustom) {
                # Our canonical subset is one unquoted resource key, no named
                # arguments. English punctuation stays in .resw, not this grammar.
                $match = [regex]::Match($value, '^\{CustomResource\s+(?<key>[^\s{},''"=]+)\}$')
                if (-not $match.Success) { throw "$Description has malformed CustomResource '$value'." }
                $key = $match.Groups['key'].Value
                $suffix = '.' + $resourceProperty
                if (-not $key.EndsWith($suffix, [StringComparison]::Ordinal)) {
                    throw "$Description CustomResource '$key' has a property mismatch for '$property'."
                }
                $prefix = $key.Substring(0, $key.Length - $suffix.Length)
                if ($prefix -cnotmatch '^[\p{L}_][\p{L}\p{Nd}_]*$') {
                    throw "$Description has malformed CustomResource key '$key'."
                }
                [void]$prefixes.Add($prefix)
                $kind = 'Resource'
            }
            elseif ($value.StartsWith('{', [StringComparison]::Ordinal)) {
                if ($value -cnotmatch '^\{(?:x:Bind|Binding|StaticResource|ThemeResource|TemplateBinding)(?:\s|\})') {
                    throw "$Description has unsupported localization markup '$value' on '$property'."
                }
                continue
            }
            else {
                if ([string]::IsNullOrWhiteSpace($value) -or $value -ceq 'OpenBlade' -or
                    $value -match '^\s*(?:%|\u00B0C|RPM|MHz|W|ms|s|dB|Kbps|Hz|\u00D7|\u00B7)\s*$' -or
                    $value -match '^\d+(?:\.\d+)?$') { continue }
                $kind = 'Literal'
            }
            $localized.Add([pscustomobject]@{
                Property = $property; ResourceProperty = $resourceProperty
                Value = $value; Kind = $kind; Key = $key; Prefix = $prefix
                Index = $tag.Groups['attributes'].Index + $attribute.Groups['value'].Index
                Length = $attribute.Groups['value'].Length
            })
        }
        if ($prefixes.Count -gt 1) { throw "$Description mixes CustomResource element keys on '$($tag.Groups['tag'].Value)'." }
        [pscustomobject]@{
            Tag = $tag.Groups['tag'].Value; Uid = $uid; Name = $name
            UidIndex = if ($null -eq $uidAttribute) { -1 } else { $tag.Groups['attributes'].Index + $uidAttribute.Index }
            UidLength = if ($null -eq $uidAttribute) { 0 } else { $uidAttribute.Length }
            Properties = @($localized.ToArray())
        }
    }
}

Export-ModuleMember -Function ConvertTo-WinUiResourceProperty, Get-WinUiLocalizationTags
