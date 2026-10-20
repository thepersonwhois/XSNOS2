local function read_line(prompt)
    io.write(prompt)
    io.flush()
    return io.read("*l")
end

local function main()
    print("XSNOS installer")
    print("This installer erases every sector on the selected disk.")
    print("The running installer disk is never a valid target.")
    print("\nDetecting block devices...")

    local disks = sys_installer_disks()
    if disks == nil then
        print("Cannot identify the disk containing the running installer; refusing to continue.")
        return
    end

    local available = {}
    for _, disk in ipairs(disks) do
        if disk.is_boot then
            print(string.format("[boot] %s  %.2f GiB (protected)", disk.name, disk.size_gib))
        else
            available[#available + 1] = disk
            print(string.format("[%d] %s  %.2f GiB", #available, disk.name, disk.size_gib))
        end
    end

    if #available == 0 then
        print("No installable disks found. Attach a separate target disk and reboot the installer.")
        return
    end

    local selection = tonumber(read_line("\nSelect disk number: "))
    if selection == nil or selection % 1 ~= 0 or selection < 1 or selection > #available then
        print("Invalid disk selection.")
        return
    end

    local target = available[selection]
    print("\nTarget: " .. target.name .. string.format(" (%.2f GiB)", target.size_gib))
    print("Layout: GPT, first partition EFI System, FAT32; remaining space split into max-size FAT32 partitions.")
    print("All sectors will be overwritten. This can take a long time.")
    print("Files from the installer volume will be copied to the first partition.")

    local confirmation = read_line("Type the disk name exactly to erase and install: ")
    if confirmation ~= target.name then
        print("Confirmation did not match; nothing was changed.")
        return
    end

    print("\nInstalling. Do not power off the machine.")
    local ok, message = sys_install(target.name)
    if ok then
        print(message)
    else
        print("Installer stopped: " .. tostring(message))
        print("The target may already have been wiped or partially partitioned.")
    end
end

local ok, err = pcall(main)
if not ok then
    print("installer: " .. tostring(err))
end