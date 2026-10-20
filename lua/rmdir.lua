local path = arg[1]
if path == nil or path == "" then
    print("usage: rmdir </path>")
    return
end

local kind = sys_pathkind(path)
if kind == nil then
    print("rmdir: not found")
    return
end

local recursive = false
if kind == "dir" then
    io.write("remove directory " .. path .. " and everything in it? (y/n) ")
    io.flush()
    if string.lower((io.read("*l") or ""):match("^%s*(.-)%s*$")) ~= "y" then
        print("cancelled.")
        return
    end
    recursive = true
end

local ok, message = sys_remove(path, recursive)
if ok then
    print("removed " .. kind .. " " .. path)
else
    print("rmdir: " .. tostring(message))
end