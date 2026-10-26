local function print_result(ok, ...)
    if ok and select('#', ...) == 1 and ... == nil then
        return
    end
    print(...)
end

while true do
    io.stdout:write("> ")
    local l = io.stdin:read "l"
    if l:match"^%s*q%s*$" then
        return
    end
    local chunk, err = load("return "..l, "=<stdin>")
    if not chunk then
        chunk, err = load(l, "=<stdin>")
    end
    if not chunk then
        print(err)
    end
    print_result(pcall(chunk))
end