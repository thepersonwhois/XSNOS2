local source = arg[1]
local destination = arg[2]
if source == nil or destination == nil then
    print("usage: xcopy <src> <dst>   e.g. xcopy /a.txt /backup/a.txt")
    return
end

local ok, message = sys_copy(source, destination)
if ok then
    print(message .. " to " .. destination)
else
    print("xcopy: " .. tostring(message))
end