local ucmd = ...
local path = ucmd.arg0
if path == nil or path == "" then
    print("usage: mkdir </path>")
    return
end

local ok, message = sys_mkdir(path)
if ok then
    print("created " .. path)
else
    print("mkdir: " .. tostring(message))
end