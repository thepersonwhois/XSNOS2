<h1>XSNOS 2 (XSNOS NX)</h1>
<p>XSNOS 2 is an operating system that is definitely not very poorly made.</p>

<h2>building</h2>
<p>go into project root and run <code>dotnet publish -c Debug -r linux-x64 -o output-x64 --verbosity minimal</code></p>

<h2>usage</h2>
<p>type hlpcm for help.</p>

<h2>making userspace programs</h2>
<p>use txted. It's just lua 5.5. assume anything not listed below in not working works, unless proven otherwise. file an issue if something doesn't work.</p>
<p>The non-stock lua syscalls are</p>
<ul>
  <li>sys_green() - makes your text GREEN, unfortunately not medium spring.</li>
  <li>sys_scrh() - gets the screen height</li>
  <li>sys_scrw() - gets the screen width</li>
  <li>sys_ver() - gets the version of xsnos2 you're on. i think this is 26.10.</li>
  <li>sys_white() - undoes sys_green(), as sys_green() is permanent and persists when your lua program closes, but why would you ever want to undo sys_green()</li>
  <li>sys_cwd() - gets the cwd because we need that</li>
  <li>sys_readkey() - reads your keys. returns key name, character (with some exceptions, like unprintable keys), and a boolean for if ctrl is held</li>
  <li>sys_goto(x,y) - this is not goto. it just flings your cursor to point x,y.</li>
  <li>sys_putat(x,y,text,cursorX,cursorY) - writes text at a position, then sends your cursor somewhere. like goto but more chris pratt-like</li>
  <li>sys_clear() - clear</li>
  <li>sys_cursor(visible) - gee i wonder it totally doesnt invisible the cursor</li>
  <li>sys_listdir(path) - lists. returns a table of {name, kind, size} entries. kind is file or dir.</li>
  <li>sys_mkdir(path), sys_chdir(path), sys_remove(path, recursive), sys_copy(source, destination) - these do exactly what you think they do.</li>
  <li>sys_partitions() - lists parts as a table. {index, name, blocks, block_size, host}</li>
  <li>sys_mkfat(index, label, type) - makes fat.</li>
  <li>sys_prtinfo() - returns {name, block_count, block_size, scheme} for the primary disk. scheme is gpt, mbr, or raw</li>
  <li>sys_prtscheme(choice) - sets partition scheme. 1 gpt 2 mbr 3 keep same</li>
  <li>sys_partition(start,count) - make a partition. returns sucess,message</li>
  <li>sys_lsdev() - lists block devices. it's a table.</li>
  <li>sys_spawn(path) - starts a lua program on a kernel thread returns process ID, or nil,error.</li>
  <li>sys_processes() - lists child lua processes with pid, path, state, and error.</li>
  <li>sys_wait(pid) - waits for a child to finish, reaps it, and returns success, exit code, which could also be error depending on if your code broke or not /hj.</li>
  <li>sys_shutdown() - unmounts filesystems, flushes storage devices, then powers off; returns false,error if power-off or storage cleanup fails.</li>
  <li>sys_gui_size() - returns the graphics canvas width and height.</li>
  <li>sys_gui_clear(r,g,b), sys_gui_pixel(x,y,r,g,b), sys_gui_line(x1,y1,x2,y2,r,g,b), sys_gui_rect(x,y,w,h,r,g,b,filled), sys_gui_text(x,y,text,r,g,b) - draw on the full-screen canvas using rgb values from 0 to 255, the best is 0, 250, 154, since that's medium spring green.</li>
  <li>sys_gui_present() - displays the current canvas frame.</li>
  <li>sys_gui_wait(milliseconds) - yields the calling thread for a short interval.</li>
  <li>sys_gui_key() - non-blocking key read; returns key, character, ctrl, or nil if no key is waiting.</li>
  <li>sys_gui_mouse() - returns mouse x, y, left, middle, and right button state.</li>
</ul>
<p>lua processes are preemptively scheduled. each process has its own lua interpreter and working directory, console and graphics hardware are shared. waited processes are reaped. it's concurrent, not isolated, so like don't expect this to be secure</p>
<p>the gui calls draw to the full-screen, so if you need graphics and a lua text console then uh figure that out yourself. drawing calls change the back buffer. call <code>sys_gui_present()</code> once per frame to show it.</p>
<p>run <code>wm</code> from the shell to start the window manager. its progs menu shows lua programs under <code>/prog/gui</code>. it is a floating window manager, it supports floating window manager things. resizing goes from the bottom corner.</p>
<p>gui programs are also lua files using the wm api. each file should return a function that receives its <code>gui</code> api table:</p>
<pre><code>return function(gui)
  gui.create_window({
    title = "Example",
    width = 480,
    height = 320,
    draw = function(ctx, window)
      ctx:clear(gui.colors.dark)
      ctx:text(8, 8, "Hello from a window", gui.colors.white)
    end,
    on_key = function(key, character, ctrl, window) end,
  })
end</code></pre>
<p>the drawing context uses window-local coordinates and provides <code>clear(color)</code>, <code>pixel(x,y,color)</code>, <code>line(x1,y1,x2,y2,color)</code>, <code>rect(x,y,w,h,color,filled)</code>, and <code>text(x,y,text,color)</code>. colors are RGB tables such as <code>{255, 255, 255}</code>; <code>gui.colors</code> includes white, black, blue, dark, and light. i somehow forgot medium spring green and am now too lazy to readd it. ::( mouse callbacks (on_mouse) receive <code>x,y,pressed,window</code> in client-local coordinates. if you truly need an example just read the programs that i have made. the window object provides <code>minimize()</code>, <code>maximize()</code>, and <code>close()</code></p>
<p>the non-working lua functions are</p>
<ul>
  <li>io.popen()</li>
  <li>os.tmpname()</li>
  <li>os.getenv()</li>
</ul>
