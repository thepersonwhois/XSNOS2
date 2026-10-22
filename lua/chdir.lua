local ucmd = ...
local path = ucmd and ucmd.arg0

if path == nil or path == "" then
    print(sys_cwd())
    return
end

local ok, message = sys_chdir(path)
if not ok then
    print("chdir: " .. tostring(message))
end