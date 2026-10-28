"""Assembles the convoy viewer: the template, the project icon, and sample replays.
Usage: build_viewer.py <template> <icon.svg> <out-artifact.html> <out-standalone.html> <label=replay.jsonl>..."""
import json, re, sys
template, icon_path, artifact_out, standalone_out, *pairs = sys.argv[1:]
icon = open(icon_path).read()
icon = re.sub(r'\s(width|height)="\d+"', '', icon, count=2)
icon = icon.replace('<svg ', '<svg role="img" aria-label="Creatura" ', 1)
icon = icon.replace('fill="#071b2b"', 'class="ink"').replace('fill="#b49345"', 'class="halo"')
samples = []
for pair in pairs:
    label, path = pair.split('=', 1)
    samples.append({"label": label, "replay": open(path).read()})
data = json.dumps(samples, ensure_ascii=False).replace('</', '<\\/')
page = open(template).read().replace('__ICON__', icon).replace('__SAMPLES__', data)
open(artifact_out, 'w').write(page)
head, body = page.split('<header class="bar">', 1)
standalone = ('<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n'
              '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n'
              '<style>[hidden]{display:none!important}body{margin:0}</style>\n'
              + head + '</head>\n<body>\n<header class="bar">' + body + '\n</body>\n</html>\n')
open(standalone_out, 'w').write(standalone)
print(f"{len(samples)} samples, artifact {len(page)//1024} KB, standalone {len(standalone)//1024} KB")
