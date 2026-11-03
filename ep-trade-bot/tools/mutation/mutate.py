"""Mutation check: break one guard at a time, run only its tests, restore.

Each entry in the JSON list names a file, an exact text that must occur once, its
replacement (plus optional further edits to the same file under "also"), the test project and a test filter. A guard is proven only when its
tests go red with it broken. Run from anywhere:

    EPTRADE_TEST_PG=... python3 tools/mutation/mutate.py tools/mutation/tg2.json
"""
import subprocess, sys, json, os, shutil
root=os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
muts=json.load(open(sys.argv[1]))
env=dict(os.environ)
out=[]
for m in muts:
    p=os.path.join(root,m['file']); src=open(p).read()
    edits=[(m['old'],m['new'])]+[(e['old'],e['new']) for e in m.get('also',[])]
    counts=[src.count(o) for o,_ in edits]
    if any(c!=1 for c in counts):
        out.append(f"{m['name']}: SKIPPED, patterns found {counts} times"); continue
    mutated=src
    for o,n in edits: mutated=mutated.replace(o,n)
    open(p,'w').write(mutated)
    try:
        r=subprocess.run(['dotnet','test',os.path.join(root,'tests',m['project']),'--filter',m['filter']],capture_output=True,text=True,env=env,timeout=900)
        txt=r.stdout+r.stderr
        line=[l for l in txt.splitlines() if 'Passed!' in l or 'Failed!' in l]
        err=[l for l in txt.splitlines() if ' error ' in l]
        res=line[0].split(' - ')[1].strip() if line else ('BUILD ERROR: '+err[0][-160:] if err else 'NO RESULT')
        out.append(f"{m['name']}: {res}")
    finally:
        open(p,'w').write(src)
print("\n".join(out))
