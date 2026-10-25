"""Regenerate the browser-friendly control registry from atlas-controls.json.

Run on your local Windows PC: py -3 sync_atlas.py
This file does not contact GitHub, launch playback, or write anywhere except
atlas-data.js in its own standalone lab directory.
"""
from __future__ import annotations
import json
from pathlib import Path
ROOT = Path(__file__).resolve().parent
src = ROOT / 'atlas-controls.json'
output = ROOT / 'atlas-data.js'
registry = json.loads(src.read_text(encoding='utf-8'))
controls = registry.get('controls')
if not isinstance(controls, list) or not controls:
    raise SystemExit('Catalog must have a non-empty controls array')
if registry.get('count') != len(controls):
    raise SystemExit('Update the count field to match controls array length')
valid_apply = {'live','prime','stream','session','build','future'}
valid_status = {'physical','fixed','observed','proposed'}
valid_kind = {'select','range','readout'}
identifiers = set()
for item in controls:
    ident = item['id']
    if not isinstance(ident,str) or not ident or not ident.replace('_','').isalnum() or ident in identifiers:
        raise SystemExit('Invalid or duplicate ID: '+repr(ident))
    identifiers.add(ident)
    if item['apply'] not in valid_apply or item['status'] not in valid_status or item['kind'] not in valid_kind:
        raise SystemExit('Invalid classification for '+ident)
    if not isinstance(item['scopes'],list) or not item['scopes'] or not isinstance(item['impacts'],list) or not item['impacts']:
        raise SystemExit('Missing impact/scenario classification for '+ident)
    options = item['options']
    if item['kind'] == 'select' and item['baseline'] not in options:
        raise SystemExit('Missing baseline in options for '+ident)
    if item['kind'] == 'range' and (not isinstance(options,list) or len(options)!=4 or options[2]<=0 or float(item['baseline'])<options[0] or float(item['baseline'])>options[1]):
        raise SystemExit('Invalid range for '+ident)
    if item['kind'] == 'readout' and options:
        raise SystemExit('Readout cannot have editable options: '+ident)
output.write_text('/* Generated from atlas-controls.json by sync_atlas.py. No native writes. */\nwindow.DESKFUSE_ATLAS = '+json.dumps(registry,ensure_ascii=False,separators=(',',':'))+';\n',encoding='utf-8')
print('Updated',output.name,'with',len(controls),'registered options. This did not change your DeskFuse repo.')
