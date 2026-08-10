import sqlite3, os
db = os.path.expandvars(r"%LOCALAPPDATA%\Orbit\data\orbit.db")
c = sqlite3.connect(db)
print("operator_runs count", c.execute("select count(*) from operator_runs").fetchone())
cols = [x[1] for x in c.execute("pragma table_info(operator_runs)")]
print("cols", cols)
for row in c.execute("select * from operator_runs order by created_at desc limit 8"):
    print(row)
print("--- suggestions")
for row in c.execute("select id, type, status, substr(coalesce(payload_json,''),1,140), created_at from agent_suggestions order by created_at desc limit 8"):
    print(row)
print("--- emails")
for row in c.execute("select id, subject, created_at from email_artifacts order by created_at desc limit 3"):
    print(row)
print("--- links")
for row in c.execute("select email_id, project_id from email_project_links"):
    print(row)
