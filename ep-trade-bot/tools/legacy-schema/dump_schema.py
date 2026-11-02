import importlib, json, os, sys, glob
os.environ.setdefault("DATABASE_URL","sqlite:///:memory:")
for k in ["LOGS_DATABASE_URL","LATENCY_DATABASE_URL","HEALTH_DATABASE_URL","SANDBOX_DATABASE_URL"]:
    os.environ.setdefault(k,"sqlite:///:memory:")
os.environ.setdefault("API_KEY_PEPPER","a"*64); os.environ.setdefault("APP_KEY","b"*64)
sys.path.insert(0,".")
mods=[p[:-3].replace("/",".") for p in sorted(glob.glob("database/*.py"))]+["broker.zerodha.database.master_contract_db"]
from sqlalchemy.orm import DeclarativeBase
tables={}
seen=set()
for m in mods:
    try: mod=importlib.import_module(m)
    except Exception as e: print("FAIL",m,e,file=sys.stderr); continue
    for name in dir(mod):
        obj=getattr(mod,name)
        import sqlalchemy as _sa
        try: md=getattr(obj,"metadata",None)
        except Exception: continue
        if not isinstance(md,_sa.MetaData): continue
        if md is None or not hasattr(md,"tables") or id(md) in seen: continue
        seen.add(id(md))
        for t in md.tables.values():
            cols=[]
            for c in t.columns:
                try: ln=getattr(c.type,"length",None); ty=str(c.type)
                except Exception: ty=type(c.type).__name__; ln=None
                cols.append(dict(name=c.name,type=ty,nullable=c.nullable,pk=c.primary_key,unique=bool(c.unique),autoinc=c.autoincrement is True or (c.primary_key and ty.startswith("INT")),
                  default=(None if c.default is None else (repr(getattr(c.default,"arg",None)) if hasattr(c.default,"arg") and not callable(c.default.arg) else "callable")), sdefault=(str(c.server_default.arg) if c.server_default is not None and hasattr(c.server_default,"arg") else None), seq=type(c.default).__name__=="Sequence",
                  fk=[str(f.target_fullname) for f in c.foreign_keys]))
            idx=[dict(name=i.name,cols=[c.name for c in i.columns],unique=i.unique) for i in t.indexes]
            uq=[dict(name=u.name,cols=[c.name for c in u.columns]) for u in t.constraints if type(u).__name__=="UniqueConstraint"]
            tables.setdefault(t.name,dict(module=m,columns=cols,indexes=idx,uniques=uq))
json.dump(tables,open(sys.argv[1],"w"),indent=1)
print(len(tables))
