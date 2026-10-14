-- E2E only: give the built-in TeamLead role read access to employees without employees.scope.all,
-- so a lead is limited to their own reporting line (G-27). Run once against the E2E database.
INSERT INTO role_permissions ("RoleId", "PermissionId")
SELECT r."Id", p."Id" FROM roles r CROSS JOIN permissions p
WHERE r."Name" = 'TeamLead' AND p."Code" = 'employees.read'
ON CONFLICT DO NOTHING;
