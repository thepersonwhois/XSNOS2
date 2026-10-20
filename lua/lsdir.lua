local path = arg[1] or sys_cwd()
local entries, err = sys_listdir(path)
if entries == nil then
    print("lsdir: " .. tostring(err))
    return
end

print(path)
for _, entry in ipairs(entries) do
    if entry.kind == "dir" then
        print("  <dir>  " .. entry.name)
    else
        print(string.format("  %8d  %s", entry.size, entry.name))
    end
end
if #entries == 0 then
    print("  (empty)")
end