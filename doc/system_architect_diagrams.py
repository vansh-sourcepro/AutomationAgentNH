"""Draws the figures used by "System Architect.docx".

    pip install pillow
    python doc/system_architect_diagrams.py        # writes doc/diagrams/*.png

The diagrams are generated rather than drawn by hand so they stay in step with the document: the
source of truth for a figure is the function below that draws it. Everything is rendered at 3x and
downsampled, which is what makes the text crisp at Word's 6.5in placement.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

# ----------------------------------------------------------------------------------- palette ---
NAVY = (31, 58, 95)
BLUE = (46, 109, 164)
TEAL = (34, 122, 122)
AMBER = (166, 114, 20)
GREEN = (47, 122, 74)
GREY = (107, 114, 128)
DARK = (33, 37, 41)
WHITE = (255, 255, 255)

FILL_NAVY = (232, 238, 246)
FILL_BLUE = (238, 244, 251)
FILL_TEAL = (233, 244, 243)
FILL_AMBER = (252, 245, 230)
FILL_GREEN = (236, 245, 239)
FILL_GREY = (245, 246, 248)

FONTS = {
    "regular": "C:/Windows/Fonts/segoeui.ttf",
    "bold": "C:/Windows/Fonts/segoeuib.ttf",
    "mono": "C:/Windows/Fonts/consola.ttf",
}
FALLBACK = "C:/Windows/Fonts/arial.ttf"

SCALE = 3


def _font(kind, size):
    path = FONTS.get(kind, FONTS["regular"])
    if not Path(path).exists():
        path = FALLBACK
    return ImageFont.truetype(path, int(size * SCALE))


class Canvas:
    """A small box-and-arrow drawing surface. Coordinates are 1x design pixels."""

    def __init__(self, width, height, background=WHITE):
        self.width, self.height = width, height
        self.image = Image.new("RGB", (width * SCALE, height * SCALE), background)
        self.draw = ImageDraw.Draw(self.image)

    # ---------------------------------------------------------------------------- primitives --
    def _s(self, *values):
        return [v * SCALE for v in values]

    def text(self, x, y, value, size=10, kind="regular", colour=DARK, anchor="lm"):
        self.draw.text(self._s(x, y), value, font=_font(kind, size), fill=colour, anchor=anchor)

    def measure(self, value, size=10, kind="regular"):
        box = _font(kind, size).getbbox(value)
        return (box[2] - box[0]) / SCALE

    def wrap(self, value, size, kind, max_width):
        words, lines, current = value.split(), [], ""
        for word in words:
            trial = f"{current} {word}".strip()
            if current and self.measure(trial, size, kind) > max_width:
                lines.append(current)
                current = word
            else:
                current = trial
        if current:
            lines.append(current)
        return lines

    def rect(self, x, y, w, h, fill=None, outline=GREY, width=1, radius=6, dash=None):
        if dash:
            self._dashed_rect(x, y, w, h, outline, width, dash)
            return
        self.draw.rounded_rectangle(
            self._s(x, y, x + w, y + h),
            radius=radius * SCALE,
            fill=fill,
            outline=outline,
            width=int(width * SCALE),
        )

    def _dashed_rect(self, x, y, w, h, colour, width, dash):
        for x1, y1, x2, y2 in (
            (x, y, x + w, y),
            (x + w, y, x + w, y + h),
            (x + w, y + h, x, y + h),
            (x, y + h, x, y),
        ):
            self.dashed_line(x1, y1, x2, y2, colour, width, dash)

    def dashed_line(self, x1, y1, x2, y2, colour=GREY, width=1, dash=6):
        length = ((x2 - x1) ** 2 + (y2 - y1) ** 2) ** 0.5
        if length == 0:
            return
        dx, dy = (x2 - x1) / length, (y2 - y1) / length
        position = 0.0
        while position < length:
            end = min(position + dash, length)
            self.draw.line(
                self._s(x1 + dx * position, y1 + dy * position, x1 + dx * end, y1 + dy * end),
                fill=colour,
                width=int(width * SCALE),
            )
            position += dash * 2

    # --------------------------------------------------------------------------------- boxes --
    def box(
        self,
        x,
        y,
        w,
        h,
        title,
        lines=(),
        fill=FILL_BLUE,
        outline=BLUE,
        title_colour=None,
        title_size=10.5,
        body_size=8.8,
        radius=6,
        accent=None,
    ):
        """A titled box with optional wrapped body lines and an optional left accent bar."""
        self.rect(x, y, w, h, fill=fill, outline=outline, width=1.2, radius=radius)
        if accent:
            self.draw.rounded_rectangle(
                self._s(x, y, x + 4, y + h), radius=2 * SCALE, fill=accent, outline=accent
            )

        title_lines = self.wrap(title, title_size, "bold", w - 18)
        body = []
        for line in lines:
            body.extend(self.wrap(line, body_size, "regular", w - 18))

        line_h = title_size * 1.35
        body_h = body_size * 1.32
        total = len(title_lines) * line_h + (len(body) * body_h + 3 if body else 0)
        cursor = y + (h - total) / 2 + line_h / 2

        for line in title_lines:
            self.text(
                x + w / 2, cursor, line, title_size, "bold", title_colour or outline, anchor="mm"
            )
            cursor += line_h
        cursor += 3
        for line in body:
            self.text(x + w / 2, cursor, line, body_size, "regular", DARK, anchor="mm")
            cursor += body_h

    def zone(self, x, y, w, h, label, colour=GREY, fill=None, dash=7, label_size=9.5):
        if fill:
            self.draw.rounded_rectangle(
                self._s(x, y, x + w, y + h), radius=9 * SCALE, fill=fill
            )
        self._dashed_rect(x, y, w, h, colour, 1.1, dash)
        pad = 6
        text_w = self.measure(label, label_size, "bold") + pad * 2
        self.draw.rectangle(self._s(x + 12, y - label_size * 0.85, x + 12 + text_w, y + label_size * 0.85), fill=WHITE)
        self.text(x + 12 + pad, y, label, label_size, "bold", colour, anchor="lm")

    # -------------------------------------------------------------------------------- arrows --
    def arrow(
        self,
        points,
        colour=NAVY,
        width=1.4,
        label=None,
        label_size=8.5,
        label_at=None,
        label_offset=(0, -9),
        dashed=False,
        heads="end",
        label_bg=WHITE,
    ):
        """Draws a polyline through `points` with arrowheads at `heads` ('end', 'both', 'none')."""
        for (x1, y1), (x2, y2) in zip(points, points[1:]):
            if dashed:
                self.dashed_line(x1, y1, x2, y2, colour, width, 5)
            else:
                self.draw.line(self._s(x1, y1, x2, y2), fill=colour, width=int(width * SCALE))

        if heads in ("end", "both"):
            self._head(points[-2], points[-1], colour)
        if heads == "both":
            self._head(points[1], points[0], colour)

        if label:
            if label_at is None:
                mid = len(points) // 2
                ax, ay = points[mid - 1], points[mid]
                label_at = ((ax[0] + ay[0]) / 2, (ax[1] + ay[1]) / 2)
            lx = label_at[0] + label_offset[0]
            ly = label_at[1] + label_offset[1]
            lines = label.split("\n")
            if label_bg:
                widest = max(self.measure(line, label_size, "regular") for line in lines)
                height = len(lines) * label_size * 1.3
                self.draw.rectangle(
                    self._s(lx - widest / 2 - 3, ly - height / 2 - 2, lx + widest / 2 + 3, ly + height / 2 + 2),
                    fill=label_bg,
                )
            start = ly - (len(lines) - 1) * label_size * 0.65
            for i, line in enumerate(lines):
                self.text(lx, start + i * label_size * 1.3, line, label_size, "regular", colour, anchor="mm")

    def _head(self, start, end, colour, size=6.5):
        (x1, y1), (x2, y2) = start, end
        length = ((x2 - x1) ** 2 + (y2 - y1) ** 2) ** 0.5
        if length == 0:
            return
        dx, dy = (x2 - x1) / length, (y2 - y1) / length
        px, py = -dy, dx
        tip = (x2, y2)
        left = (x2 - dx * size + px * size * 0.5, y2 - dy * size + py * size * 0.5)
        right = (x2 - dx * size - px * size * 0.5, y2 - dy * size - py * size * 0.5)
        self.draw.polygon(
            [self._s(*tip), self._s(*left), self._s(*right)], fill=colour
        )

    def save(self, path):
        self.image.resize((self.width, self.height), Image.LANCZOS).save(path, dpi=(220, 220))
        return path


# ================================================================================== figures ====
def fig_context(path):
    """System context: who talks to what, and across which boundary."""
    c = Canvas(1120, 700)

    c.zone(28, 60, 1064, 500, "CLIENT PREMISES — SINGLE WINDOWS SERVER (ONE INSTALLATION PER CLIENT)", NAVY)

    # people
    c.box(60, 92, 190, 62, "Planner / Shop user", ["Works in the ERP as today"], FILL_GREY, GREY, GREY)
    c.box(60, 172, 190, 62, "Automation administrator", ["Owns the agent dashboard"], FILL_GREY, GREY, GREY)

    # ERP zone
    c.zone(290, 92, 380, 440, "EXISTING ERP  (UNCHANGED)", BLUE, FILL_BLUE)
    c.box(312, 122, 336, 78, "Angular 7 ERP Web UI", ["Existing screens", "+ new Automation Dashboard"], WHITE, BLUE)
    c.box(312, 226, 336, 78, "ERP Application API", [".NET Core 2 · IIS", "business rules, validation, audit"], WHITE, BLUE)
    c.box(312, 330, 336, 74, "ERP Database", ["SQL Server — owned by the ERP"], WHITE, BLUE)
    c.box(312, 428, 336, 62, "ERP identity", ["POST /api/v1/auth/login → JWT"], FILL_NAVY, NAVY)

    c.arrow([(480, 200), (480, 226)], BLUE)
    c.arrow([(480, 304), (480, 330)], BLUE)

    # Agent zone
    c.zone(712, 92, 356, 440, "AUTOMATION AGENT  (NEW)", TEAL, FILL_TEAL)
    c.box(732, 122, 316, 108, "NewHorizon Automation Agent", [".NET 10 Windows Service", "workflow engine · scheduler", "job queue · checkpoints"], WHITE, TEAL)
    c.box(732, 254, 316, 78, "Management / read API", ["Kestrel · loopback only :5080", "API key required"], WHITE, TEAL)
    c.box(732, 356, 316, 78, "Automation Database", ["SQL Server —", "separate, owned by the agent"], WHITE, TEAL)
    c.box(732, 458, 316, 56, "AI advisory (optional)", ["recommend only — never executes"], FILL_AMBER, AMBER)

    c.arrow([(890, 230), (890, 254)], TEAL)
    c.arrow([(890, 332), (890, 356)], TEAL)
    c.arrow([(890, 434), (890, 458)], AMBER, dashed=True)

    # cross-boundary
    c.arrow(
        [(732, 176), (648, 176)],
        NAVY,
        label="Agent → ERP\nHTTPS, JWT bearer\n(execute automation)",
        label_at=(690, 176),
        label_offset=(0, -34),
    )
    c.arrow(
        [(648, 288), (732, 288)],
        NAVY,
        label="ERP → Agent\nHTTP + API key\n(status, control)",
        label_at=(690, 288),
        label_offset=(0, 32),
    )

    c.box(430, 596, 300, 62, "Azure AI / Cognitive Services", ["Outbound HTTPS · optional", "consumed by the agent only"], FILL_AMBER, AMBER)
    c.arrow([(890, 514), (890, 627), (730, 627)], AMBER, dashed=True)

    c.text(560, 578, "The agent never opens the ERP database, and the ERP never opens the agent's.",
           9, "bold", NAVY, anchor="mm")

    return c.save(path)


def fig_layers(path):
    """Clean Architecture: dependencies point inward only."""
    c = Canvas(1120, 630)

    band = [
        ("NewHorizon.Automation.Worker", "Windows Service host · Kestrel · minimal API · Serilog · options validation", NAVY, FILL_NAVY),
        ("NewHorizon.Automation.Infrastructure    |    NewHorizon.Automation.ErpClient",
         "EF Core, repositories, hosted services   |   typed ERP client, Polly, auth handler",
         BLUE, FILL_BLUE),
        ("NewHorizon.Automation.Application", "workflow definitions · WorkflowEngine · use cases · ports (IErpClient, IJobRepository, …)", TEAL, FILL_TEAL),
        ("NewHorizon.Automation.Domain", "entities · state machine · idempotency key — no project references at all", GREEN, FILL_GREEN),
    ]

    x, w = 110, 740
    y, h, gap = 70, 88, 26
    for i, (title, subtitle, colour, fill) in enumerate(band):
        inset = i * 26
        c.box(x + inset, y + i * (h + gap), w - inset * 2, h, title, [subtitle], fill, colour,
              accent=colour, title_size=10.2, body_size=8.6)

    for i in range(3):
        top = y + i * (h + gap) + h
        c.arrow([(480, top), (480, top + gap)], GREY, width=1.6)

    c.text(892, 120, "Dependencies", 10, "bold", NAVY, anchor="lm")
    c.text(892, 138, "point inward only", 9.5, "regular", GREY, anchor="lm")
    c.arrow([(948, 170), (948, 430)], NAVY, width=1.8)
    c.text(974, 300, "purity increases", 9, "regular", GREY, anchor="lm")

    c.box(96, 520, 928, 62,
          "Extensibility rule: a new workflow is a new WorkflowDefinition in Application — the engine, queue, retry, logging and API surface do not change.",
          [], FILL_GREY, GREY, NAVY, title_size=9.6)

    return c.save(path)


def fig_components(path):
    """Runtime components and every connection between them."""
    c = Canvas(1320, 770)

    c.zone(290, 40, 800, 660, "AGENT PROCESS  (ONE WINDOWS SERVICE)", TEAL, (250, 252, 252))

    # consumers and sinks, outside the process
    c.box(40, 60, 220, 74, "Angular 7 Dashboard", ["workflow status, errors,", "enable / disable the agent"], FILL_BLUE, BLUE)
    c.box(40, 176, 220, 66, "ERP API (.NET Core 2)", ["proxies the dashboard's calls"], FILL_BLUE, BLUE)
    c.box(40, 480, 220, 60, "Log files", ["logs\\agent-*.log"], FILL_GREY, GREY)

    # column A — hosted services
    c.box(330, 190, 240, 66, "CycleSchedulerService", ["timer — enqueues one cycle"], WHITE, TEAL)
    c.box(330, 280, 240, 66, "JobDispatcherService", ["claims Pending jobs, N in parallel"], WHITE, TEAL)
    c.box(330, 370, 240, 66, "OrphanRecoveryService", ["reclaims jobs left Running"], WHITE, TEAL)
    c.box(330, 480, 240, 60, "Serilog", ["rolling file + console"], WHITE, GREY, GREY)
    c.box(330, 580, 240, 100, "Registered separately",
          ["AddAutomationHostedServices()", "so a test host composes the same", "application without a live timer"], FILL_GREY, GREY, NAVY, title_size=9.4)

    # column B — the core
    c.box(610, 56, 240, 74, "Minimal API endpoints", ["/api/automation/* · /health", "ApiKeyFilter on every route"], WHITE, NAVY)
    c.box(610, 250, 240, 88, "WorkflowEngine", ["walks stages → operations,", "checkpoints after each one"], FILL_TEAL, TEAL)
    c.box(610, 370, 240, 62, "WorkflowCatalog", ["AutoShopCycle + future workflows"], FILL_TEAL, TEAL)
    c.box(610, 464, 240, 62, "IDecisionService (AI)", ["advisory only — never executes"], FILL_AMBER, AMBER)

    # column C — adapters
    c.box(880, 190, 200, 70, "JobRepository", ["claim · checkpoint · query"], WHITE, NAVY)
    c.box(880, 286, 200, 62, "AutomationConfigRepository", ["runtime settings per module"], WHITE, NAVY)
    c.box(880, 376, 200, 76, "HttpErpClient", ["typed calls + Polly", "+ ErpAuthHandler"], WHITE, NAVY)
    c.box(880, 478, 200, 62, "ErpTokenProvider", ["one cached JWT per process"], FILL_NAVY, NAVY)
    c.box(880, 568, 200, 62, "ErpLoginStartupService", ["warms the token at startup"], WHITE, TEAL)

    # externals, right
    c.box(1130, 190, 170, 100, "Automation Database", ["SQL Server", "PGTPL_", "AutomationAgent"], FILL_GREEN, GREEN)
    c.box(1130, 376, 170, 100, "ERP Application API", ["HTTPS", "existing endpoints"], FILL_BLUE, BLUE)

    # wiring — orthogonal, with reserved gutters at x=305 / 586 / 866 and lanes at y=93/145/160
    c.arrow([(150, 134), (150, 176)], BLUE)
    c.arrow([(260, 209), (305, 209), (305, 93), (610, 93)], BLUE,
            label="HTTP + X-Api-Key", label_at=(430, 93), label_offset=(0, -11))
    c.arrow([(730, 130), (730, 250)], NAVY, label="run-now · retry\nresume · cancel", label_at=(730, 190), label_offset=(78, 0))
    c.arrow([(850, 93), (1020, 93), (1020, 190)], NAVY, label="read", label_at=(1020, 140), label_offset=(22, 0))
    c.arrow([(570, 223), (586, 223), (586, 160), (940, 160), (940, 190)], TEAL,
            label="enqueue", label_at=(760, 160), label_offset=(0, -11))
    c.arrow([(570, 403), (586, 403), (586, 145), (900, 145), (900, 190)], TEAL)
    c.arrow([(570, 313), (592, 313), (592, 294), (610, 294)], TEAL, label="run", label_at=(592, 275), label_offset=(0, -8))
    c.arrow([(610, 320), (600, 320), (600, 495), (610, 495)], AMBER, dashed=True)
    c.arrow([(850, 270), (866, 270), (866, 240), (880, 240)], NAVY, label="checkpoint", label_at=(866, 218), label_offset=(0, -8))
    c.arrow([(850, 300), (872, 300), (872, 317), (880, 317)], NAVY)
    c.arrow([(850, 330), (860, 330), (860, 414), (880, 414)], NAVY)
    c.arrow([(730, 338), (730, 370)], TEAL)
    c.arrow([(980, 452), (980, 478)], NAVY)
    c.arrow([(980, 568), (980, 540)], TEAL)
    c.arrow([(1080, 225), (1130, 225)], GREEN, label="TCP 1433", label_at=(1105, 225), label_offset=(0, -11))
    c.arrow([(1080, 317), (1105, 317), (1105, 270), (1130, 270)], GREEN)
    c.arrow([(1080, 414), (1130, 414)], BLUE, label="HTTPS", label_at=(1105, 414), label_offset=(0, -11))
    c.arrow([(1080, 509), (1110, 509), (1110, 450), (1130, 450)], NAVY)
    c.arrow([(330, 510), (260, 510)], GREY)

    return c.save(path)


def fig_cycle(path):
    """The AutoShop cycle, stage by stage."""
    c = Canvas(1140, 730)

    c.box(40, 60, 200, 70, "Timer tick", ["CycleSchedulerService", "every N seconds"], FILL_GREY, GREY, NAVY)
    c.box(40, 168, 200, 70, "POST /run-now", ["administrator, on demand"], FILL_GREY, GREY, NAVY)
    c.box(300, 110, 220, 76, "Enqueue cycle", ["one live cycle only —", "UX_AutomationJob_LiveCycle"], FILL_NAVY, NAVY)
    c.box(580, 110, 220, 76, "Claim & run", ["JobDispatcherService", "UPDLOCK, READPAST"], FILL_NAVY, NAVY)

    c.arrow([(240, 95), (270, 95), (270, 148), (300, 148)], NAVY)
    c.arrow([(240, 203), (270, 203), (270, 148), (300, 148)], NAVY)
    c.arrow([(520, 148), (580, 148)], NAVY)
    c.arrow([(690, 186), (690, 214), (280, 214), (280, 250)], NAVY)

    stages = [
        ("Stage 1 — OafToSjo", ["CreateSjoFromPendingOaf", "GET pending OAF → query-before-create → POST SJO", "no pending OAF is a quiet skip, not a failure"], TEAL, FILL_TEAL),
        ("Stage 2 — Discovery", ["DiscoverSites", "GET the site list, then expand the plan to", "one step per site for both per-site stages"], TEAL, FILL_TEAL),
        ("Stage 3 — SjoSequence  (once per site)", ["SequenceSite — GET the site's SJO rows, sort by delivery date ascending,", "set the selection flag, POST back. Rows travel as JsonObject, so nothing", "the ERP sent is dropped on the way back."], BLUE, FILL_BLUE),
        ("Stage 4 — AutoShop  (once per site)", ["AutoShopSite — GET, build the body, POST"], BLUE, FILL_BLUE),
    ]
    top, height, gap = 250, 84, 26
    for i, (title, lines, colour, fill) in enumerate(stages):
        y = top + i * (height + gap)
        c.box(100, y, 700, height, title, lines, fill, colour, accent=colour)
        if i < len(stages) - 1:
            c.arrow([(450, y + height), (450, y + height + gap)], GREY, width=1.6)

    c.box(830, 250, 262, 84, "Checkpoint after every operation",
          ["one AutomationJobStep row —", "status · ErpDocumentRef · payloads"], FILL_GREEN, GREEN)
    c.arrow([(800, 292), (830, 292)], GREEN, dashed=True)

    c.box(830, 360, 262, 84, "Resume is unambiguous",
          ["the first operation whose", "status is not Completed"], FILL_GREEN, GREEN)
    c.arrow([(800, 402), (830, 402)], GREEN, dashed=True)

    c.box(830, 470, 262, 84, "One step per site",
          ["a failure at the seventh site", "resumes at the seventh site"], FILL_GREEN, GREEN)
    c.arrow([(800, 512), (830, 512)], GREEN, dashed=True)

    return c.save(path)


def fig_states(path):
    """Job lifecycle."""
    c = Canvas(1120, 580)

    red = (176, 60, 60)
    c.box(60, 210, 170, 62, "Pending", ["queued in the database"], FILL_GREY, GREY, NAVY)
    c.box(330, 210, 180, 62, "Running", ["claimed by a worker"], FILL_NAVY, NAVY)
    c.box(620, 70, 200, 62, "AwaitingApproval", ["Partial-mode gate"], FILL_AMBER, AMBER)
    c.box(620, 350, 200, 62, "Failed", ["retry / resume available"], (253, 238, 238), red)
    c.box(900, 210, 170, 62, "Completed", ["terminal"], FILL_GREEN, GREEN)
    c.box(900, 350, 170, 62, "Cancelled", ["terminal"], FILL_GREY, GREY, GREY)

    c.arrow([(230, 241), (330, 241)], NAVY, label="claim", label_offset=(0, -11))
    c.arrow([(510, 241), (900, 241)], GREEN, label="every operation Completed", label_at=(705, 241), label_offset=(0, -11))
    c.arrow([(450, 210), (450, 101), (620, 101)], AMBER, label="approval gate", label_at=(535, 101), label_offset=(0, -11))
    c.arrow([(620, 120), (560, 120), (560, 225), (510, 225)], AMBER,
            label="approve  (actor + remarks)", label_at=(560, 165), label_offset=(-92, 0))
    c.arrow([(430, 272), (430, 381), (620, 381)], red, label="business refusal, or\ntransient retries exhausted",
            label_at=(548, 381), label_offset=(0, -24))
    c.arrow([(620, 362), (560, 362), (560, 258), (510, 258)], red,
            label="retry / resume", label_at=(560, 310), label_offset=(-52, 0))
    c.arrow([(390, 272), (390, 460), (985, 460), (985, 412)], GREY, label="cancel", label_at=(690, 460), label_offset=(0, -11))

    c.box(60, 490, 1000, 62,
          "resume is failure recovery. approve / reject are business decisions and record actor + remarks for audit — the approval UI never calls resume.",
          [], FILL_BLUE, BLUE, NAVY, title_size=9.6)

    return c.save(path)


def fig_erd(path):
    """The automation database."""
    c = Canvas(1140, 720)

    def table(x, y, w, name, rows, colour, fill):
        header = 30
        h = header + len(rows) * 19 + 10
        c.rect(x, y, w, h, fill=WHITE, outline=colour, width=1.3, radius=5)
        c.draw.rounded_rectangle(c._s(x, y, x + w, y + header), radius=5 * SCALE, fill=colour)
        c.draw.rectangle(c._s(x, y + header - 6, x + w, y + header), fill=colour)
        c.text(x + 10, y + header / 2, name, 10, "bold", WHITE, anchor="lm")
        for i, (col, kind) in enumerate(rows):
            ry = y + header + 12 + i * 19
            c.text(x + 10, ry, col, 8.6, "bold" if kind == "PK" or kind == "FK" else "regular",
                   colour if kind in ("PK", "FK") else DARK, anchor="lm")
            if kind in ("PK", "FK", "UX"):
                c.text(x + w - 10, ry, kind, 7.6, "bold", colour, anchor="rm")
        return h

    table(70, 60, 330, "AutomationJob", [
        ("Id  uniqueidentifier", "PK"),
        ("CorrelationId, WorkflowType", ""),
        ("DocumentType, DocumentId", ""),
        ("Mode, Priority, Status, CurrentStage", ""),
        ("IdempotencyKey  nchar(64)", "UX"),
        ("RetryCount, NotBeforeUtc", ""),
        ("ApprovedBy / At, CancelledBy / Reason", ""),
        ("Created / Started / CompletedAtUtc", ""),
        ("RowVersion  rowversion", ""),
    ], NAVY, FILL_NAVY)

    table(70, 330, 330, "AutomationJobStep", [
        ("Id  uniqueidentifier", "PK"),
        ("JobId → AutomationJob", "FK"),
        ("Stage, OperationName, Sequence", "UX"),
        ("Kind, Target (Site ID)", ""),
        ("Status, RetryCount", ""),
        ("Request / ResponsePayload", ""),
        ("ErpDocumentRef, Remarks", ""),
        ("ApprovedBy / At, Started / CompletedAtUtc", ""),
    ], TEAL, FILL_TEAL)

    table(560, 60, 330, "AutomationLog", [
        ("Id  uniqueidentifier", "PK"),
        ("JobId, StepId", "FK"),
        ("CorrelationId, Module", ""),
        ("ApiEndpoint", ""),
        ("StartedAtUtc, CompletedAtUtc", ""),
        ("DurationMs  bigint", ""),
        ("Result", ""),
    ], BLUE, FILL_BLUE)

    table(560, 300, 330, "AutomationError", [
        ("Id  uniqueidentifier", "PK"),
        ("JobId, StepId", "FK"),
        ("ErrorType  (Transient / Business / …)", ""),
        ("TechnicalMessage  nvarchar(max)", ""),
        ("LaymanMessage  nvarchar(1000)", ""),
        ("StackTrace, ApiEndpoint", ""),
        ("CreatedAtUtc", ""),
    ], (176, 60, 60), (253, 238, 238))

    table(560, 528, 330, "AutomationConfig", [
        ("Id  uniqueidentifier", "PK"),
        ("Module  (SJO / OAF / … / AutoShopCycle)", "UX"),
        ("EnableAgent, EnableModule, IsLicensed", ""),
        ("Mode, WorkingHoursStart / End", ""),
        ("RetryCount, ParallelWorkers, intervals", ""),
        ("Payload / Log / ErrorRetentionDays", ""),
        ("UpdatedAtUtc, UpdatedBy", ""),
    ], GREEN, FILL_GREEN)

    c.arrow([(235, 265), (235, 330)], NAVY, label="1 : N  cascade delete", label_at=(235, 298), label_offset=(0, -10))
    c.arrow([(400, 120), (560, 120)], BLUE, label="1 : N", label_offset=(0, -10))
    c.arrow([(400, 380), (480, 380), (480, 160), (560, 160)], BLUE)
    c.arrow([(400, 150), (500, 150), (500, 340), (560, 340)], (176, 60, 60), label="1 : N", label_at=(500, 250), label_offset=(-26, 0))
    c.arrow([(400, 420), (520, 420), (520, 380), (560, 380)], (176, 60, 60))
    c.box(70, 528, 330, 130, "Database: PGTPL_AutomationAgent",
          ["SQL Server · owned solely by the agent",
           "READ_COMMITTED_SNAPSHOT ON",
           "schema by EF Core migrations, or by",
           "deploy/sql/001_Schema.sql where dotnet ef",
           "cannot run on the server"], FILL_GREY, GREY, NAVY)

    c.dashed_line(555, 578, 425, 578, GREEN, 1.3, 6)
    c.text(490, 564, "read at each job start", 8.4, "regular", GREEN, anchor="mm")

    return c.save(path)


def fig_auth(path):
    """How a token is obtained, attached, refreshed and replayed."""
    c = Canvas(1140, 640)

    lanes = [
        (70, 250, "Operation code", FILL_TEAL, TEAL),
        (360, 250, "ErpAuthHandler\n(DelegatingHandler)".replace("\n", " "), FILL_NAVY, NAVY),
        (650, 250, "ErpTokenProvider\n(singleton cache)".replace("\n", " "), FILL_NAVY, NAVY),
        (915, 175, "ERP /api/v1/auth/login", FILL_BLUE, BLUE),
    ]
    for x, w, title, fill, colour in lanes:
        c.box(x, 56, w, 52, title, [], fill, colour)
        c.dashed_line(x + w / 2, 108, x + w / 2, 520, colour, 1, 6)

    def step(y, x1, x2, text, colour=NAVY, dashed=False):
        c.arrow([(x1, y), (x2, y)], colour, label=text, label_at=((x1 + x2) / 2, y), label_offset=(0, -11), dashed=dashed)

    step(150, 195, 485, "1  send request (no token in sight)", TEAL)
    step(196, 485, 775, "2  get a valid token")
    step(242, 775, 1005, "3  POST userName / password / connStr", BLUE)
    step(288, 1005, 775, "4  200 + { token.value, validTo }", BLUE, dashed=True)
    step(334, 775, 485, "5  cached token (24 h, absolute validTo)", NAVY, dashed=True)
    step(380, 485, 1005, "6  Authorization: Bearer …", NAVY)
    step(426, 1005, 485, "7  401 once → re-authenticate and replay exactly once", (176, 60, 60), dashed=True)
    step(472, 485, 195, "8  response — or ErpAuthenticationException", TEAL, dashed=True)

    c.box(70, 528, 500, 82, "Why one provider",
          ["A stampede of parallel workers causes one login, not N.",
           "Refresh happens inside a two-minute margin, so a token",
           "cannot lapse mid-request."], FILL_GREEN, GREEN)
    c.box(600, 528, 470, 82, "Why the body decides, not the status",
          ["A refusal arrives as HTTP 400 with success:false and a",
           "message key (InvalidUsernamePasswordKey) — the provider",
           "reads the envelope before classifying the outcome."], FILL_AMBER, AMBER)

    return c.save(path)


def fig_deployment(path):
    """What is installed where."""
    c = Canvas(1140, 700)

    c.zone(40, 60, 700, 580, "CLIENT WINDOWS SERVER (2019 / 2022)", NAVY)

    c.zone(66, 100, 330, 250, "IIS", BLUE, FILL_BLUE)
    c.box(86, 132, 290, 62, "ERP Web (Angular 7)", ["static bundle · port 80 / 443"], WHITE, BLUE)
    c.box(86, 212, 290, 62, "ERP API (.NET Core 2)", ["existing application pool"], WHITE, BLUE)
    c.text(231, 300, "unchanged by this project", 8.8, "bold", BLUE, anchor="mm")
    c.arrow([(231, 194), (231, 212)], BLUE)

    c.zone(66, 386, 330, 216, "WINDOWS SERVICES", TEAL, FILL_TEAL)
    c.box(86, 420, 290, 96, "NewHorizon Automation Agent", ["self-contained .NET 10 publish", "Automatic (Delayed Start)", "recovery: restart on failure"], WHITE, TEAL)
    c.box(86, 530, 290, 54, "logs\\agent-*.log", ["Serilog rolling file"], WHITE, GREY, GREY)
    c.arrow([(231, 516), (231, 530)], GREY)

    c.zone(430, 100, 288, 502, "SQL SERVER INSTANCE", GREEN, (247, 251, 248))
    c.box(450, 140, 248, 96, "ERP database", ["e.g. PGTPL_MihiR_A_11062026", "agent has no login here"], WHITE, BLUE)
    c.box(450, 268, 248, 110, "PGTPL_AutomationAgent", ["jobs · steps · logs · errors · config", "least-privilege SQL login:", "db_datareader + db_datawriter"], WHITE, GREEN)
    c.box(450, 410, 248, 90, "Backup / retention", ["nightly full + log backups", "retention windows in config"], FILL_GREY, GREY, GREY)
    c.arrow([(450, 320), (414, 320), (414, 450), (376, 450)], GREEN, heads="both", label="1433", label_at=(414, 392), label_offset=(-20, 0))
    c.text(430, 616, "The agent has no SQL login on the ERP database — the boundary is enforced by", 8.4, "bold", (176, 60, 60), anchor="mm")
    c.text(430, 632, "SQL Server permissions, not by developer convention.", 8.4, "bold", (176, 60, 60), anchor="mm")

    c.box(790, 130, 310, 96, "Agent management API", ["http://localhost:5080", "loopback binding only", "X-Api-Key on every route"], FILL_NAVY, NAVY)
    c.arrow([(376, 243), (410, 243), (410, 82), (945, 82), (945, 130)], NAVY, heads="both",
            label="ERP → agent  (loopback HTTP + API key)", label_at=(680, 82), label_offset=(0, -11))

    c.box(790, 262, 310, 82, "Agent → ERP", ["https://<erp-host>/api/v1/…", "JWT from the ERP login"], FILL_BLUE, BLUE)
    c.arrow([(790, 303), (700, 303)], BLUE, heads="none", dashed=True)

    c.box(790, 380, 310, 116, "Install / update / uninstall",
          ["deploy\\install.ps1 — publish, install, start",
           "deploy\\update.ps1 — stop, replace, restart",
           "deploy\\uninstall.ps1 — stop and remove",
           "deploy\\sql\\001_Schema.sql — idempotent DDL"], FILL_GREY, GREY, NAVY)

    c.box(790, 522, 310, 96, "Verification",
          ["GET /api/automation/health",
           "checks.database + checks.erpApi",
           "ERP login line in the log at startup"], FILL_GREEN, GREEN)

    return c.save(path)


def fig_workflow_extensibility(path):
    """How a second workflow is added."""
    c = Canvas(1120, 430)

    c.box(60, 90, 250, 110, "Write a WorkflowDefinition",
          ["stages, in order", "operations inside each stage", "≈ one file in Application"], FILL_TEAL, TEAL, accent=TEAL)
    c.box(370, 90, 230, 110, "Register it in WorkflowCatalog",
          ["one line"], FILL_TEAL, TEAL, accent=TEAL)
    c.box(660, 90, 230, 110, "Seed an AutomationConfig row",
          ["module name, mode,", "enable / disable, retention"], FILL_TEAL, TEAL, accent=TEAL)
    c.arrow([(310, 145), (370, 145)], TEAL)
    c.arrow([(600, 145), (660, 145)], TEAL)

    c.box(60, 250, 830, 120, "Everything else is already built and is not touched",
          ["queue and claiming  ·  parallel workers  ·  checkpoints and resume  ·  retry with backoff and jitter",
           "idempotency  ·  error capture with layman messages  ·  Serilog logging  ·  management API  ·  dashboard",
           "That is the test of the architecture: if adding a workflow required engine changes, the design would have failed."],
          FILL_GREY, GREY, NAVY)
    c.arrow([(475, 200), (475, 250)], NAVY, width=1.6)

    c.box(930, 90, 150, 280, "Candidate\nnext workflows".replace("\n", " "),
          ["MIL", "CBOM verification", "Purchase requisition", "Labor requisition", "Allocation /", "de-allocation"], FILL_AMBER, AMBER)

    return c.save(path)


FIGURES = {
    "context.png": fig_context,
    "layers.png": fig_layers,
    "components.png": fig_components,
    "cycle.png": fig_cycle,
    "states.png": fig_states,
    "erd.png": fig_erd,
    "auth.png": fig_auth,
    "deployment.png": fig_deployment,
    "extensibility.png": fig_workflow_extensibility,
}


def render_all(output_dir):
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    written = []
    for name, function in FIGURES.items():
        written.append(function(output_dir / name))
    return written


if __name__ == "__main__":
    for path in render_all(Path(__file__).resolve().parent / "diagrams"):
        print(f"written: {path}")
