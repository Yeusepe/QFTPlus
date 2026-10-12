"""Validate community RESX files before the build reads them."""
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
RESOURCES = ROOT / 'src/app/Localization'
CULTURES = set('af ar ca zh-Hans zh-Hant cs da nl fi fr de el he hu it ja ko nb pl pt pt-BR ro ru sr-Cyrl es sv tr uk vi'.split())
TOKEN = re.compile(r'\{\d+(?:,-?\d+)?(?::[^{}]+)?\}')


def read_strings(data):
    text = data.decode('utf-8-sig')
    if '<!DOCTYPE' in text.upper() or '<!ENTITY' in text.upper():
        raise ValueError('DTD/entity declarations are not permitted')
    root = ET.fromstring(text)
    if root.tag != 'root':
        raise ValueError('Expected RESX root')
    strings, seen = {}, set()
    for entry in root:
        if entry.tag == 'resheader' or entry.tag == '{http://www.w3.org/2001/XMLSchema}schema':
            continue
        if entry.tag != 'data' or set(entry.attrib) - {'name', '{http://www.w3.org/XML/1998/namespace}space'}:
            raise ValueError('Only plain string resources are permitted')
        key = entry.get('name', '')
        if not key or key.casefold() in seen:
            raise ValueError(f'Duplicate or empty key: {key}')
        seen.add(key.casefold())
        values = entry.findall('value')
        if len(values) != 1 or list(values[0]) or any(child.tag not in {'value', 'comment'} for child in entry):
            raise ValueError(f'Expected plain text value: {key}')
        strings[key] = values[0].text or ''
    return strings


def placeholders(text):
    tokens, index = [], 0
    while index < len(text):
        if text[index:index + 2] in ('{{', '}}'):
            index += 2
        elif text[index] == '{':
            match = TOKEN.match(text, index)
            if not match:
                raise ValueError('Invalid .NET placeholder')
            tokens.append(match[0])
            index = match.end()
        elif text[index] == '}':
            raise ValueError('Unescaped closing brace')
        else:
            index += 1
    return sorted(tokens)


def validate_translation(source, translated):
    for key, value in translated.items():
        if key not in source:
            raise ValueError(f'Unknown source key: {key}')
        if not value.strip():
            raise ValueError(f'Empty translation: {key}; omit untranslated entries')
        if placeholders(value) != placeholders(source[key]):
            raise ValueError(f'Changed placeholders: {key}')


def check():
    source = read_strings((RESOURCES / 'Strings.resx').read_bytes())
    if not source:
        raise ValueError('The English source catalog is empty')
    for key, value in source.items():
        if not value:
            raise ValueError(f'Empty English source: {key}')
        placeholders(value)
    translated_files = list(RESOURCES.glob('Strings.*.resx'))
    for path in translated_files:
        culture = path.name[len('Strings.'):-len('.resx')]
        if culture not in CULTURES:
            raise ValueError(f'Unsupported culture in {path.name}; check Crowdin language mapping')
        try:
            validate_translation(source, read_strings(path.read_bytes()))
        except ValueError as error:
            raise ValueError(f'{path.name}: {error}') from error
    for path in (ROOT / 'src/app').iterdir():
        if path.suffix not in {'.cs', '.xaml'}:
            continue
        for match in re.finditer(r'Loc\.(?:Get|Format)\("([^"]+)"|\{local:Loc ([^}]+)\}', path.read_text(encoding='utf-8-sig')):
            key = match[1] or match[2]
            if key not in source:
                raise ValueError(f'{path.name}: missing resource {key}')
    print(f'Validated {len(source)} source strings and {len(translated_files)} translation files.')


if __name__ == '__main__':
    try:
        check()
    except (ValueError, ET.ParseError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
