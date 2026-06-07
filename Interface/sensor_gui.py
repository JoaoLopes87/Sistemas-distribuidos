import tkinter as tk
import socket
import threading
import time
import sqlite3
import os
import queue
import json
import tkinter.font as tkfont
from datetime import datetime
import pika

BG       = '#F5F5F5'
CARD     = '#F5F5F5'
CARD2    = '#EBE8E8'
BORDER   = '#DDDADA'
INK      = '#121212'
INK2     = '#74787E'
INK3     = '#A9ACB1'
ACC      = '#121212'
ACC_D    = '#2A2A2A'
ACC_S    = '#F0F0F0'
HAIR     = '#E2E2E2'
OK       = '#43A877'
OK_S     = '#E9F4EE'
WARN     = '#BD8A33'
WARN_S   = '#F7F0DF'
CRIT     = '#C1514A'
CRIT_S   = '#FAE9E6'

SENSOR_DOT = {
    'TEMP':  '#C1514A',
    'HUM':   '#3F6FA3',
    'RUIDO': '#7C63BF',
    'PM2.5': '#BD8A33',
    'PM10':  '#A9711F',
    'LUM':   '#2F9E8F',
}
SENSOR_LABEL = {
    'TEMP':  'Temperatura',
    'HUM':   'Humidade',
    'RUIDO': 'Ruído',
    'PM2.5': 'PM2.5',
    'PM10':  'PM10',
    'LUM':   'Luminosidade',
}
SENSOR_HINT = {
    'TEMP':  'ex: 20c  ·  70f  ·  300k',
    'HUM':   'ex: 65  ·  65%',
    'RUIDO': 'ex: 85  ·  85db',
    'PM2.5': 'ex: 41',
    'PM10':  'ex: 62',
    'LUM':   'ex: 500  ·  500lux',
}
SENSOR_ORDER = ['TEMP', 'HUM', 'RUIDO', 'PM2.5', 'PM10', 'LUM']

GW_IP   = '127.0.0.1'
GW_PORT = 5002
DB_PATH = os.path.join(os.path.dirname(__file__), '..', 'Servidor', 'sensors.db')
FONT_FACE = 'Inter'

def F(size=12, weight='normal', mono=False):
    return (FONT_FACE, size, weight)


def _rounded_rectangle(canvas, x1, y1, x2, y2, radius, **kwargs):
    radius = max(0, min(radius, (x2 - x1) / 2, (y2 - y1) / 2))
    points = [
        x1 + radius, y1, x2 - radius, y1, x2, y1, x2, y1 + radius,
        x2, y2 - radius, x2, y2, x2 - radius, y2, x1 + radius, y2,
        x1, y2, x1, y2 - radius, x1, y1 + radius, x1, y1,
    ]
    return canvas.create_polygon(points, smooth=True, splinesteps=36, **kwargs)


class RoundedFrame(tk.Canvas):
    def __init__(self, master, *, bg, fill, outline=None, radius=18,
                 border_width=1, padding=1, **kwargs):
        super().__init__(
            master, bg=bg, bd=0, highlightthickness=0, relief='flat', **kwargs
        )
        self._fill = fill
        self._outline = outline if outline is not None else fill
        self._radius = radius
        self._border_width = border_width
        self._padding = padding
        self.body = tk.Frame(self, bg=fill)
        self._window = self.create_window(padding, padding, anchor='nw', window=self.body)
        self.bind('<Configure>', self._redraw)
        self.body.bind('<Configure>', self._fit_height)

    def _fit_height(self, _event=None):
        wanted = self.body.winfo_reqheight() + self._padding * 2
        if wanted != self.winfo_reqheight():
            self.configure(height=wanted)

    def _redraw(self, event):
        self.delete('rounded-bg')
        inset = max(1, self._border_width)
        _rounded_rectangle(
            self, inset, inset, event.width - inset, event.height - inset,
            self._radius, fill=self._fill, outline=self._outline,
            width=self._border_width, tags='rounded-bg'
        )
        self.tag_lower('rounded-bg')
        self.coords(self._window, self._padding, self._padding)
        self.itemconfigure(
            self._window,
            width=max(1, event.width - self._padding * 2),
            height=max(1, event.height - self._padding * 2),
        )

    def set_colors(self, *, fill, outline=None):
        self._fill = fill
        self._outline = outline if outline is not None else fill
        self.body.configure(bg=fill)
        self._redraw(type('Event', (), {
            'width': self.winfo_width(), 'height': self.winfo_height()
        })())


class RoundedButton(tk.Canvas):
    def __init__(self, master, *, text, command, font, bg, fill, active_fill,
                 fg='white', radius=14, height=48):
        super().__init__(
            master, bg=bg, height=height, bd=0, highlightthickness=0,
            relief='flat', cursor='arrow'
        )
        self._text = text
        self._command = command
        self._font = font
        self._fill = fill
        self._active_fill = active_fill
        self._fg = fg
        self._radius = radius
        self._state = 'normal'
        self.bind('<Configure>', self._draw)
        self.bind('<Enter>', lambda _: self._draw(fill=self._active_fill))
        self.bind('<Leave>', lambda _: self._draw())
        self.bind('<Button-1>', self._click)

    def _draw(self, _event=None, fill=None):
        self.delete('all')
        color = fill or self._fill
        if self._state == 'disabled':
            color = ACC_D
        _rounded_rectangle(
            self, 1, 1, self.winfo_width() - 1, self.winfo_height() - 1,
            self._radius, fill=color, outline=color
        )
        self.create_text(
            self.winfo_width() / 2, self.winfo_height() / 2,
            text=self._text, font=self._font, fill=self._fg
        )

    def _click(self, _event):
        if self._state == 'normal':
            self._command()

    def config(self, **kwargs):
        self._text = kwargs.pop('text', self._text)
        self._state = kwargs.pop('state', self._state)
        super().config(**kwargs)
        self._draw()

    configure = config


class RoundedActionButton(tk.Canvas):
    def __init__(self, master, *, text, command, dot, meta='', state='normal',
                 fg=INK, meta_fg=INK3, outline=HAIR, fill=BG, height=48):
        super().__init__(
            master, bg=BG, height=height, bd=0, highlightthickness=0,
            relief='flat', cursor='arrow'
        )
        self._text = text
        self._command = command
        self._dot = dot
        self._meta = meta
        self._state = state
        self._fg = fg
        self._meta_fg = meta_fg
        self._outline = outline
        self._fill = fill
        self.bind('<Configure>', self._draw)
        self.bind('<Enter>', lambda _: self._draw(hover=True))
        self.bind('<Leave>', lambda _: self._draw())
        self.bind('<Button-1>', self._click)

    def _draw(self, _event=None, hover=False):
        self.delete('all')
        disabled = self._state == 'disabled'
        fill = '#F0F0F0' if hover and not disabled else self._fill
        _rounded_rectangle(
            self, 1, 1, self.winfo_width() - 1, self.winfo_height() - 1,
            16, fill=fill, outline=self._outline, width=1
        )
        mid = self.winfo_height() / 2
        self.create_oval(19, mid - 4, 27, mid + 4, fill=self._dot, outline='')
        self.create_text(
            39, mid, text=self._text, anchor='w',
            font=F(13, 'bold' if not disabled else 'normal'),
            fill=INK3 if disabled else self._fg
        )
        if self._meta:
            self.create_text(
                self.winfo_width() - 18, mid, text=self._meta, anchor='e',
                font=F(9), fill=INK3 if disabled else self._meta_fg
            )

    def _click(self, _event):
        if self._state == 'normal':
            self._command()

    def config(self, **kwargs):
        self._state = kwargs.pop('state', self._state)
        self._fg = kwargs.pop('fg', self._fg)
        self._meta = kwargs.pop('meta', self._meta)
        kwargs.pop('cursor', None)
        super().config(**kwargs)
        self._draw()

    configure = config


class HeartbeatIndicator(tk.Canvas):
    def __init__(self, master):
        super().__init__(
            master, width=132, height=30, bg=BG, bd=0,
            highlightthickness=0, relief='flat'
        )
        self._running = False
        self._offset = 0
        self._after_id = None
        self.bind('<Configure>', self._draw)

    def _draw(self, _event=None):
        self.delete('all')
        _rounded_rectangle(
            self, 1, 1, 131, 29, 14,
            fill=CARD2, outline=CARD2
        )
        points = [
            10, 15, 38, 15, 42, 15, 45, 6, 49, 24,
            53, 10, 57, 15, 78, 15, 82, 15, 85, 6,
            89, 24, 93, 10, 97, 15, 105, 15,
        ]
        self.create_line(
            points, fill='#D8D8D8', width=1.3,
            capstyle='round', joinstyle='round'
        )
        self._sweep = self.create_line(
            points, fill=OK, width=1.8, dash=(10, 90),
            dashoffset=self._offset, capstyle='round', joinstyle='round'
        )
        self.create_text(118, 15, text='ok', font=F(9, mono=True), fill=INK3)

    def start(self):
        if self._running:
            return
        self._running = True
        self._animate()

    def stop(self):
        self._running = False
        if self._after_id is not None:
            self.after_cancel(self._after_id)
            self._after_id = None
        self._offset = 0
        self._draw()

    def ack(self):
        self.itemconfigure(self._sweep, fill=OK, width=2.4)
        self.after(250, lambda: self.itemconfigure(self._sweep, width=1.8))

    def _animate(self):
        if not self._running:
            return
        self._offset = (self._offset - 2) % 100
        self.itemconfigure(self._sweep, dashoffset=self._offset)
        self._after_id = self.after(45, self._animate)


class SensorApp:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title('sensor.client')
        self.root.configure(bg=BG)
        self.root.minsize(900, 650)
        self.root.resizable(True, True)

        global FONT_FACE
        families = set(tkfont.families(self.root))
        FONT_FACE = 'Inter' if 'Inter' in families else 'Helvetica Neue'

        self.sock       = None
        self.rfile      = None
        self.wfile      = None
        self.sock_lock  = threading.Lock()
        self.sensor_id  = ''
        self.sensor_zona = ''
        self.allowed_types    = []
        self.types_registered = False
        self.connected        = False
        self._hb_stop         = threading.Event()
        self._start_time      = 0.0
        self._ui_queue        = queue.Queue()

        self._build_all()
        self._show_connection()
        self.root.after(20, self._process_ui_queue)
        self.root.mainloop()

    def _ui_call(self, callback):
        self._ui_queue.put(callback)

    def _ui_call_later(self, delay, callback):
        self._ui_queue.put(lambda: self.root.after(delay, callback))

    def _process_ui_queue(self):
        try:
            while True:
                self._ui_queue.get_nowait()()
        except queue.Empty:
            pass
        self.root.after(20, self._process_ui_queue)

    def _build_all(self):
        self._frame_conn = self._build_connection_frame()
        self._frame_main = self._build_main_frame()

    # ── Screen 1 ───────────────────────────────────────────────────────────────
    def _build_connection_frame(self):
        f = tk.Frame(self.root, bg=BG)

        # App bar
        bar = tk.Frame(f, bg=BG)
        bar.pack(fill='x', padx=96, pady=(52, 0))
        tk.Label(bar, text='Sensor.Client', font=F(11),
                 bg=BG, fg=INK).pack(side='left')
        self._conn_status_lbl = tk.Label(bar, text='Desligado', font=F(11),
                                          bg=BG, fg=INK)
        self._conn_status_lbl.pack(side='right')

        # Formulário central, sem cartão exterior, como no layout do Figma.
        form = tk.Frame(f, bg=BG, width=440)
        form.place(relx=0.5, rely=0.52, anchor='center', width=440)
        form.columnconfigure(0, weight=1)

        tk.Label(form, text='Sensor id', font=F(12, 'bold'),
                 bg=BG, fg=INK).grid(row=0, column=0, sticky='w', pady=(0, 10))

        entry_shell = RoundedFrame(
            form, bg=BG, fill=CARD2, outline=CARD2, radius=20,
            border_width=0, padding=8, height=56
        )
        entry_shell.grid(row=1, column=0, sticky='ew')
        self._entry_id = tk.Entry(
            entry_shell.body, font=F(13), bg=CARD2, fg=INK,
            bd=0, highlightthickness=0, insertbackground=INK, relief='flat'
        )
        self._entry_id.pack(fill='x', padx=10, pady=4, ipady=5)
        self._entry_id.insert(0, 'S101')
        self._entry_id.bind('<Return>', lambda _: self._on_connect())

        # O aviso só ocupa espaço quando existe uma mensagem.
        self._err_card = RoundedFrame(
            form, bg=BG, fill=CRIT_S, outline=CRIT_S, radius=18,
            border_width=0, padding=2
        )
        self._err_card.grid(row=2, column=0, sticky='ew', pady=(12, 0))
        self._err_lbl = tk.Label(self._err_card.body, text='', font=F(11),
                                  bg=CRIT_S, fg=CRIT, wraplength=410, justify='left',
                                  anchor='w')
        self._err_lbl.pack(fill='x', padx=12, pady=10)
        self._err_card.grid_remove()

        self._btn_connect = RoundedButton(
            form, text='Ligar Ao Gateway', font=F(11, 'bold'),
            bg=BG, fill=ACC, active_fill=ACC_D, radius=22, height=48,
            command=self._on_connect
        )
        self._btn_connect.grid(row=3, column=0, sticky='ew', pady=(44, 0))

        tk.Label(form, text=f'Gateway  {GW_IP} : {GW_PORT}',
                 font=F(10), bg=BG, fg=INK2).grid(
            row=4, column=0, pady=(30, 0))

        return f

    # ── Screen 2 ───────────────────────────────────────────────────────────────
    def _build_main_frame(self):
        f = tk.Frame(self.root, bg=BG)

        # App bar
        bar = tk.Frame(f, bg=BG)
        bar.pack(fill='x', padx=24, pady=(18, 0))
        self._main_brand = tk.Label(bar, text='■  sensor.client', font=F(14),
                                     bg=BG, fg=INK2)
        self._main_brand.pack(side='left')
        self._heartbeat = HeartbeatIndicator(bar)
        self._heartbeat.pack(side='left', padx=(14, 0))
        self._uptime_lbl = tk.Label(bar, text='uptime 00:00', font=F(12),
                                     bg=BG, fg=INK3)
        self._uptime_lbl.pack(side='right', padx=(0, 14))
        self._main_status = tk.Label(bar, text='● Ligado', font=F(14, 'bold'), bg=BG, fg=OK)
        self._main_status.pack(side='right')

        # Two columns
        cols = tk.Frame(f, bg=BG)
        cols.pack(fill='both', expand=True, padx=24, pady=14)

        # ── Left: actions panel ───────────────────────────────────────────────
        left = tk.Frame(cols, bg=CARD, width=340)
        left.pack(side='left', fill='y', padx=(0, 20))
        left.pack_propagate(False)

        # Group: Envio de dados
        tk.Label(left, text='ENVIO DE DADOS', font=F(11, 'bold'), bg=CARD, fg=INK3).pack(
            anchor='w', padx=8, pady=(8, 8))

        self._action_btns = {}
        for stype in SENSOR_ORDER:
            btn = RoundedActionButton(
                left, text=SENSOR_LABEL[stype], dot=SENSOR_DOT[stype],
                meta='bloqueado', state='disabled',
                command=lambda t=stype: self._on_send_data(t)
            )
            btn.pack(fill='x', padx=1, pady=3)
            self._action_btns[stype] = btn

        # Group: Sistema
        tk.Label(left, text='SISTEMA', font=F(11, 'bold'), bg=CARD, fg=INK3).pack(
            anchor='w', padx=8, pady=(14, 8))

        self._btn_types = RoundedActionButton(
            left, text='Registar Tipos', dot=ACC, meta='ativa envios',
            fg=ACC, outline='#BFCFE0', fill='#EEF3F9',
            command=self._on_register_types
        )
        self._btn_types.pack(fill='x', padx=1, pady=3)

        self._btn_video = RoundedActionButton(
            left, text='Video Request', dot=OK, meta='bloqueado',
            state='disabled',
            command=self._on_video
        )
        self._btn_video.pack(fill='x', padx=1, pady=3)

        self._btn_analises = RoundedActionButton(
            left, text='Ver Análises', dot=OK, meta='abrir',
            fg=OK, outline='#CBE3D7',
            command=self._on_analyses
        )
        self._btn_analises.pack(fill='x', padx=1, pady=3)

        self._btn_disc = RoundedActionButton(
            left, text='Desligar', dot=CRIT, fg=CRIT,
            outline='#E7C3C0', command=self._on_disconnect
        )
        self._btn_disc.pack(fill='x', padx=1, pady=(14, 4))

        # ── Right: log panel ──────────────────────────────────────────────────
        right_shell = RoundedFrame(
            cols,
            bg=BG,
            fill='white',
            outline=HAIR,
            radius=24,
            border_width=1,
            padding=4   
        )
        right_shell.pack(side='left', fill='both', expand=True)
        right = right_shell.body

        log_bar = tk.Frame(right, bg='white')
        log_bar.pack(fill='x', padx=14, pady=(8, 4))
        tk.Label(log_bar, text='LOG DE COMUNICAÇÃO', font=F(11, 'bold'),
                 bg='white', fg=INK3).pack(side='left')
        clear_lbl = tk.Label(
            log_bar,
            text="limpar",
            font=F(11),
            bg=CARD,
            fg=INK3,
            cursor="hand2"
        )

        clear_lbl.pack(side="right")
        clear_lbl.bind("<Button-1>", lambda e: self._clear_log())

        self._log = tk.Text(
            right, font=F(12), bg='white', fg=INK2,
            bd=0, state='disabled', wrap='word',
            selectbackground=ACC_S, padx=12, pady=8
        )
        sb = tk.Scrollbar(right, command=self._log.yview,
                          bg='white', troughcolor='white',
                          activebackground=HAIR, bd=0, highlightthickness=0,
                          relief='flat', elementborderwidth=0)
        self._log.configure(yscrollcommand=sb.set)
        sb.pack(side='right', fill='y')
        self._log.pack(fill='both', expand=True, padx=(4, 0), pady=(4, 10))

        self._log.tag_configure('ts',   foreground=INK3)
        self._log.tag_configure('sent', foreground=ACC)
        self._log.tag_configure('ok',   foreground=OK)
        self._log.tag_configure('err',  foreground=CRIT)
        self._log.tag_configure('warn', foreground=WARN)
        self._log.tag_configure('info', foreground=INK3)

        return f

    # ═══════════════════════════════════════════════════════════════════════════
    # NAVIGATION
    # ═══════════════════════════════════════════════════════════════════════════

    def _show_connection(self):
        self._frame_main.pack_forget()
        self._heartbeat.stop()
        self.root.geometry('1280x640')
        self._frame_conn.pack(fill='both', expand=True)

    def _show_main(self):
        self._frame_conn.pack_forget()
        self.root.geometry('1280x750')
        self._main_brand.config(text=f'■  sensor.client — {self.sensor_id}')
        self._main_status.config(text='● Ligado', fg=OK)
        self._frame_main.pack(fill='both', expand=True)
        self._heartbeat.start()
        self._start_time = time.time()
        self._tick_uptime()


    def _tick_uptime(self):
        if not self.connected:
            return
        elapsed = int(time.time() - self._start_time)
        m, s = divmod(elapsed, 60)
        self._uptime_lbl.config(text=f'uptime {m:02d}:{s:02d}')
        self.root.after(1000, self._tick_uptime)



    def _on_connect(self):
        sid = self._entry_id.get().strip()
        if not sid:
            self._show_conn_err('Introduza um ID de sensor.')
            return
        self._hide_conn_err()
        self._btn_connect.config(state='disabled', text='A ligar…')
        threading.Thread(target=self._connect_thread, args=(sid,), daemon=True).start()

    def _connect_thread(self, sid):
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(5)
            s.connect((GW_IP, GW_PORT))
            s.settimeout(None)
            rfile = s.makefile('r')
            wfile = s.makefile('w')

            msg = f'HELLO | {sid}'
            wfile.write(msg + '\n')
            wfile.flush()
            resp = rfile.readline().strip()

            if not resp.startswith('OK'):
                s.close()
                self._ui_call(lambda: self._connect_failed(resp))
                return

            # Parse "OK | ZONA_NORTE | TEMP,HUM,RUIDO" (novo) ou "OK | TEMP,HUM,RUIDO" (antigo)
            parts = resp.split('|')
            if len(parts) >= 3:
                zona  = parts[1].strip()
                types = [t.strip() for t in parts[2].split(',') if t.strip()]
            elif len(parts) >= 2:
                zona  = ''
                types = [t.strip() for t in parts[1].split(',') if t.strip()]
            else:
                zona  = ''
                types = []

            self.sock = s
            self.rfile = rfile
            self.wfile = wfile
            self.sensor_id = sid
            self.sensor_zona = zona
            self.allowed_types = types
            self.types_registered = False
            self.connected = True

            self._ui_call(self._connect_ok)
            self._ui_call(lambda: self._append_log('ok',   f'handshake aceite · {sid}'))
            self._ui_call(lambda: self._append_log('info', f'tipos permitidos: {", ".join(types)}'))
            self._ui_call(lambda: self._append_log('info', 'tipos não registados — envios bloqueados'))

            self._hb_stop.clear()
            threading.Thread(target=self._heartbeat_loop, daemon=True).start()

        except Exception as e:
            err = str(e)
            self._ui_call(lambda: self._connect_failed(err))

    def _connect_ok(self):
        self._btn_connect.config(state='normal', text='Ligar Ao Gateway')
        self._show_main()

    def _connect_failed(self, msg):
        self._btn_connect.config(state='normal', text='Ligar Ao Gateway')
        self._show_conn_err(msg or 'Ligação recusada pelo Gateway.')

    # ── Connection error banner ────────────────────────────────────────────────
    def _show_conn_err(self, msg):
        self._err_card.set_colors(fill=CRIT_S, outline=CRIT_S)
        self._err_card.grid()
        self._err_lbl.config(text=f'!  {msg}', bg=CRIT_S, fg=CRIT)

    def _hide_conn_err(self):
        self._err_lbl.config(text='')
        self._err_card.grid_remove()

    # ═══════════════════════════════════════════════════════════════════════════
    # DISCONNECT
    # ═══════════════════════════════════════════════════════════════════════════

    def _on_disconnect(self):
        threading.Thread(target=self._disconnect_thread, daemon=True).start()

    def _disconnect_thread(self):
        self._hb_stop.set()
        self._send_recv(f'DISCONNECT | {self.sensor_id}')
        self._teardown_socket()
        self._ui_call(self._after_disconnect)

    def _teardown_socket(self):
        self.connected = False
        try:
            if self.sock:
                self.sock.close()
        except Exception:
            pass
        self.sock  = None
        self.rfile = None
        self.wfile = None

    def _after_disconnect(self):
        self._main_status.config(text='● Desligado', fg=CRIT)
        self._conn_status_lbl.config(text='Desligado', fg=INK)
        self._reset_buttons()
        self._show_connection()

    # ═══════════════════════════════════════════════════════════════════════════
    # REGISTER TYPES
    # ═══════════════════════════════════════════════════════════════════════════

    def _on_register_types(self):
        threading.Thread(target=self._register_types_thread, daemon=True).start()

    def _register_types_thread(self):
        types_str = ','.join(self.allowed_types)
        resp = self._send_recv(f'TYPES | {self.sensor_id} | {types_str}')
        if resp is None:
            return
        if resp == 'OK':
            self.types_registered = True
            self._ui_call(self._unlock_buttons)
            self._ui_call(lambda: self._append_log('ok', f'tipos registados: {types_str}'))
        else:
            r = resp
            self._ui_call(lambda: self._append_log('err', f'TYPES rejeitado: {r}'))
            self._ui_call(lambda: self._show_error_dialog('Registar Tipos', r))

    def _unlock_buttons(self):
        for stype, btn in self._action_btns.items():
            if stype in self.allowed_types:
                btn.config(state='normal', fg=INK, meta='ativo', cursor='arrow')
        self._btn_types.config(meta='registado')
        self._btn_video.config(state='normal', fg=INK2, meta='ativo')

    def _reset_buttons(self):
        for btn in self._action_btns.values():
            btn.config(state='disabled', fg=INK3, meta='bloqueado')
        self._btn_types.config(meta='ativa envios')
        self._btn_video.config(state='disabled', fg=INK3, meta='bloqueado')

    # ═══════════════════════════════════════════════════════════════════════════
    # SEND DATA
    # ═══════════════════════════════════════════════════════════════════════════

    def _on_send_data(self, stype):
        self._open_value_dialog(stype)

    def _open_value_dialog(self, stype):

        dlg = tk.Toplevel(self.root)
        dlg.title(SENSOR_LABEL[stype])
        dlg.configure(bg=BG)
        dlg.resizable(False, False)
        dlg.transient(self.root)

        w = 640
        h = 340

        self._center(dlg, w, h)

        card = RoundedFrame(
            dlg,
            bg=BG,
            fill='white',
            outline=HAIR,
            radius=24,
            border_width=1,
            padding=0
        )
        card.pack(fill='both', expand=True, padx=24, pady=24)

        body = card.body

        tk.Label(
            body,
            text=SENSOR_LABEL[stype],
            font=F(20, 'bold'),
            bg='white',
            fg=INK
        ).pack(anchor='w', padx=60, pady=(40, 30))

        entry_shell = RoundedFrame(
            body,
            bg='white',
            fill='#F1ECEC',
            outline='#F1ECEC',
            radius=16,
            border_width=0,
            padding=0,
            height=64
        )

        entry_shell.pack(fill='x', padx=60)

        entry = tk.Entry(
            entry_shell.body,
            font=F(16),
            bg='#F1ECEC',
            fg=INK,
            bd=0,
            relief='flat',
            highlightthickness=0,
            insertbackground=INK,
            justify='center'
        )

        entry.pack(fill='both', expand=True, ipady=12)
        entry.focus_set()

        tk.Label(
            body,
            text=SENSOR_HINT[stype].replace('ex:', 'Ex:'),
            font=F(11),
            bg='white',
            fg=INK2
        ).pack(pady=(12, 28))

        def submit():
            val = entry.get().strip()
            if not val:
                return

            dlg.destroy()

            threading.Thread(
                target=self._send_data_thread,
                args=(stype, val),
                daemon=True
            ).start()

        btn = RoundedButton(
            body,
            text='Enviar',
            command=submit,
            font=F(12, 'bold'),
            bg='white',
            fill='#111111',
            active_fill='#222222',
            radius=14,
            height=56
        )

        btn.pack(fill='x', padx=60)

        entry.bind('<Return>', lambda e: submit())

    def _send_data_thread(self, stype, value):
        ts    = datetime.now().strftime('%Y-%m-%dT%H:%M:%S')
        label = SENSOR_LABEL[stype]
        ok    = self._publish_rabbitmq(stype, value, ts)
        if ok:
            self._ui_call(lambda: self._append_log('ok', f'SENSOR → RABBITMQ [sensor.{self.sensor_zona}.{stype}]: {value}'))
            self._ui_call_later(600, self._load_analyses_silent)
        else:
            self._ui_call(lambda: self._append_log('err', f'Falha ao publicar {label} no RabbitMQ'))

    def _publish_rabbitmq(self, stype, value, timestamp):
        try:
            credentials = pika.PlainCredentials('admin', 'password123')
            params = pika.ConnectionParameters('localhost', credentials=credentials)
            conn = pika.BlockingConnection(params)
            ch = conn.channel()
            ch.exchange_declare(exchange='sensor_data', exchange_type='topic', durable=False)
            msg = json.dumps({
                'SensorId':  self.sensor_id,
                'Zona':      self.sensor_zona,
                'Tipo':      stype,
                'Valor':     value,
                'Timestamp': timestamp
            })
            routing_key = f'sensor.{self.sensor_zona}.{stype}'
            ch.basic_publish(exchange='sensor_data', routing_key=routing_key, body=msg.encode())
            conn.close()
            return True
        except Exception as e:
            self._ui_call(lambda err=str(e): self._append_log('err', f'RabbitMQ: {err}'))
            return False

    # ═══════════════════════════════════════════════════════════════════════════
    # VIDEO REQUEST
    # ═══════════════════════════════════════════════════════════════════════════

    def _on_video(self):
        threading.Thread(target=self._video_thread, daemon=True).start()

    def _video_thread(self):
        resp = self._send_recv(f'VIDEO_REQUEST | {self.sensor_id}')
        if resp == 'ACK':
            self._ui_call(lambda: self._append_log('ok', 'video request aceite'))
        elif resp is not None:
            r = resp
            self._ui_call(lambda: self._append_log('err', f'video request rejeitado: {r}'))

    # ═══════════════════════════════════════════════════════════════════════════
    # HEARTBEAT
    # ═══════════════════════════════════════════════════════════════════════════

    def _heartbeat_loop(self):
        while not self._hb_stop.wait(30):
            if not self.connected:
                break
            resp = self._send_recv(f'HEARTBEAT | {self.sensor_id}')
            if resp == 'ALIVE':
                self._ui_call(self._flash_hb)
            elif resp is None:
                break

    def _flash_hb(self):
        self._heartbeat.ack()

    # ═══════════════════════════════════════════════════════════════════════════
    # SEND / RECEIVE (thread-safe)
    # ═══════════════════════════════════════════════════════════════════════════

    def _send_recv(self, msg):
        try:
            with self.sock_lock:
                if not self.wfile or not self.rfile:
                    return None
                self._ui_call(lambda m=msg: self._append_log('sent', f'SENSOR → GATEWAY: {m}'))
                self.wfile.write(msg + '\n')
                self.wfile.flush()
                resp = self.rfile.readline().strip()
                level = 'ok' if resp in ('OK', 'ACK', 'STORED', 'ALIVE', 'BYE') or resp.startswith('OK') else 'err'
                self._ui_call(lambda r=resp, lv=level: self._append_log(lv, f'GATEWAY → SENSOR: {r}'))
                return resp
        except Exception as e:
            err = str(e)
            self._teardown_socket()
            self._ui_call(lambda: self._append_log('err', f'Ligação perdida: {err}'))
            self._ui_call(lambda: self._show_error_dialog('Ligação perdida', err))
            self._ui_call(self._after_disconnect)
            return None

    # ═══════════════════════════════════════════════════════════════════════════
    # LOG
    # ═══════════════════════════════════════════════════════════════════════════

    def _append_log(self, level, text):
        ts = datetime.now().strftime('%H:%M:%S')
        self._log.config(state='normal')
        self._log.insert('end', f'{ts}  ', ('ts',))
        self._log.insert('end', text + '\n', (level,))
        self._log.config(state='disabled')
        self._log.see('end')

    def _clear_log(self):
        self._log.config(state='normal')
        self._log.delete('1.0', 'end')
        self._log.config(state='disabled')

    # ═══════════════════════════════════════════════════════════════════════════
    # ANALYSES
    # ═══════════════════════════════════════════════════════════════════════════

    def _on_analyses(self):
        threading.Thread(target=self._fetch_analyses, args=(True,), daemon=True).start()

    def _load_analyses_silent(self):
        threading.Thread(target=self._fetch_analyses, args=(False,), daemon=True).start()

    def _fetch_analyses(self, force_open):
        db = os.path.abspath(DB_PATH)
        if not os.path.exists(db):
            if force_open:
                self._ui_call(lambda: self._show_error_dialog(
                    'Base de dados', f'Ficheiro não encontrado:\n{db}'))
            return
        try:
            con = sqlite3.connect(db)
            cur = con.cursor()
            cur.execute('''
                SELECT analysis_type, resultado, timestamp
                FROM analises WHERE sensor_id = ?
                ORDER BY id DESC LIMIT 30
            ''', (self.sensor_id,))
            rows = cur.fetchall()
            con.close()
            self._ui_call(lambda: self._open_analyses_window(rows))
        except Exception as e:
            if force_open:
                err = str(e)
                self._ui_call(lambda: self._show_error_dialog('Análises', err))

    def _open_analyses_window(self, rows):
        win = tk.Toplevel(self.root)
        win.title(f'Análises — {self.sensor_id}')
        win.configure(bg=BG)
        win.resizable(False, False)
        win.transient(self.root)
        self._center(win, 680, 540)

        # App bar
        bar = tk.Frame(win, bg=BG)
        bar.pack(fill='x', padx=24, pady=(18, 0))
        tk.Label(bar, text='◼  sensor.client — análises', font=F(12, mono=True),
                 bg=BG, fg=INK2).pack(side='left')
        tk.Label(bar, text=f'● Ligado', font=F(12, 'bold'), bg=BG, fg=OK).pack(side='right')
        tk.Label(bar, text=self.sensor_id, font=F(10, mono=True), bg=BG, fg=INK3).pack(
            side='right', padx=(0, 14))

        # Group rows by analysis type
        by_type = {'ANALISE': [], 'POLUICAO': [], 'RISCO': []}
        for atype, resultado, ts in rows:
            if atype in by_type:
                by_type[atype].append((resultado, ts))

        SECTION_LABEL = {'ANALISE': 'ANÁLISE', 'POLUICAO': 'POLUIÇÃO', 'RISCO': 'RISCO'}

        body = tk.Frame(win, bg=BG)
        body.pack(fill='both', expand=True, padx=24, pady=12)

        for key in ('ANALISE', 'POLUICAO', 'RISCO'):
            items = by_type[key]

            # Determine level from most recent result text
            level = 'NORMAL'
            if items:
                r = items[0][0].upper()
                if any(w in r for w in ('CRÍTICO', 'CRITICO', 'ALTO')):
                    level = 'CRITICO'
                elif any(w in r for w in ('AVISO', 'MÉDIO', 'MEDIO', 'ATENÇÃO', 'ATENCAO', 'MODERADA', 'DEGRADADA')):
                    level = 'AVISO'

            lvl_fg = {'NORMAL': OK, 'AVISO': WARN, 'CRITICO': CRIT}[level]
            lvl_bg = {'NORMAL': OK_S, 'AVISO': WARN_S, 'CRITICO': CRIT_S}[level]

            card = tk.Frame(body, bg=CARD, highlightthickness=1, highlightbackground=BORDER)
            card.pack(fill='x', pady=5)

            # Card header
            hdr = tk.Frame(card, bg=CARD)
            hdr.pack(fill='x', padx=14, pady=8)
            tk.Label(hdr, text=SECTION_LABEL[key], font=F(12, 'bold'), bg=CARD, fg=INK).pack(side='left')
            badge = tk.Label(hdr, text=f'● {level}', font=F(10, 'bold'),
                              bg=lvl_bg, fg=lvl_fg, padx=8, pady=3)
            badge.pack(side='right')

            # Rows
            if items:
                for resultado, ts in items[:4]:
                    row = tk.Frame(card, bg=CARD2)
                    row.pack(fill='x', padx=14, pady=1)
                    tk.Label(row, text=ts, font=F(9, mono=True), bg=CARD2, fg=INK3,
                             anchor='w', width=18).pack(side='left', padx=(6, 8), pady=4)
                    tk.Label(row, text=resultado, font=F(10), bg=CARD2, fg=INK,
                              anchor='w', wraplength=490, justify='left').pack(
                        side='left', fill='x', expand=True, pady=4, padx=(0, 8))
            else:
                tk.Label(card, text='Sem dados ainda para este sensor.',
                         font=F(11), bg=CARD, fg=INK3).pack(pady=(0, 10))

        RoundedButton(
            win, text='Fechar', font=F(12, 'bold'),
            bg=BG, fill='#111111', active_fill='#222222',
            radius=14, height=56,
            command=win.destroy
        ).pack(fill='x', padx=24, pady=(0, 16))

    # ═══════════════════════════════════════════════════════════════════════════
    # ERROR DIALOG
    # ═══════════════════════════════════════════════════════════════════════════

    def _show_error_dialog(self, title, message):
        dlg = tk.Toplevel(self.root)
        dlg.title('Erro')
        dlg.configure(bg=BG)
        dlg.resizable(False, False)
        dlg.transient(self.root)
        self._center(dlg, 420, 230)

        card = tk.Frame(dlg, bg=CRIT_S, highlightthickness=1, highlightbackground=CRIT)
        card.pack(fill='both', expand=True, padx=16, pady=16)

        # Header
        hdr = tk.Frame(card, bg=CRIT_S)
        hdr.pack(fill='x', padx=16, pady=(14, 0))
        tk.Label(hdr, text=' ! ', font=F(13, 'bold'), bg=CRIT, fg='white',
                 pady=4).pack(side='left', padx=(0, 10))
        tk.Label(hdr, text='Erro do Gateway', font=F(13, 'bold'),
                 bg=CRIT_S, fg=CRIT).pack(side='left')

        tk.Label(card, text=message, font=F(11), bg=CRIT_S, fg=INK,
                  wraplength=370, justify='left').pack(padx=16, pady=(10, 4), anchor='w')

        tk.Label(card, text=f'gateway › {message}', font=F(9, mono=True),
                  bg=CRIT_S, fg=CRIT).pack(padx=16, pady=(0, 4), anchor='w')

        tk.Button(card, text='Fechar', font=F(12, 'bold'), bg=CRIT, fg='white',
                  activebackground='#a33f38', bd=0, cursor='arrow', pady=9,
                  command=dlg.destroy).pack(fill='x', padx=16, pady=(10, 14))

    # ═══════════════════════════════════════════════════════════════════════════
    # UTILITY
    # ═══════════════════════════════════════════════════════════════════════════

    def _center(self, win, w, h):
        win.update_idletasks()
        rx = self.root.winfo_x() + (self.root.winfo_width()  - w) // 2
        ry = self.root.winfo_y() + (self.root.winfo_height() - h) // 2
        win.geometry(f'{w}x{h}+{rx}+{ry}')


if __name__ == '__main__':
    SensorApp()
