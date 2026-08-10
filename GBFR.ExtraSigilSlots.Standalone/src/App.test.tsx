import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import App from "./App";
import { mockControls } from "./api";

async function connectToGame(): Promise<ReturnType<typeof userEvent.setup>> {
  const user = userEvent.setup();
  await waitFor(() => expect(screen.getByTestId("process-row-18244")).toBeInTheDocument());
  await user.click(screen.getByTestId("process-row-18244"));
  await user.click(screen.getByRole("button", { name: "连接" }));
  await waitFor(() => expect(screen.getByRole("heading", { name: "槽位选择" })).toBeInTheDocument());
  return user;
}

describe("Standalone workbench", () => {
  beforeEach(() => {
    mockControls.reset();
  });

  afterEach(() => {
    cleanup();
  });

  it("selects a detected process before connecting", async () => {
    const user = userEvent.setup();
    render(<App />);
    await waitFor(() => expect(screen.getByTestId("process-row-18244")).toBeInTheDocument());
    const processRow = screen.getByTestId("process-row-18244");
    expect(processRow).toHaveAttribute("aria-pressed", "false");
    await user.click(processRow);
    expect(processRow).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByText("注入前会再次核验选定进程。", { exact: false })).toBeInTheDocument();
  });

  it("automatically injects and connects when exactly one game process is detected", async () => {
    mockControls.setDetectedProcessCount(1);
    render(<App />);
    await waitFor(() => expect(screen.getByRole("heading", { name: "槽位选择" })).toBeInTheDocument());
    expect(screen.getByText("已自动检测、注入并连接游戏。")).toBeInTheDocument();
  });

  it("keeps an explicitly disconnected single process disconnected", async () => {
    mockControls.setDetectedProcessCount(1);
    render(<App />);
    const user = userEvent.setup();
    await waitFor(() => expect(screen.getByRole("heading", { name: "槽位选择" })).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "断开" }));
    await waitFor(() => expect(screen.getByText("已暂停对此进程的自动连接", { exact: false })).toBeInTheDocument());
    await new Promise((resolve) => window.setTimeout(resolve, 1_100));
    expect(screen.getByRole("heading", { name: "连接正在运行的游戏" })).toBeInTheDocument();
  });

  it("renders the connected main layout", async () => {
    render(<App />);
    await connectToGame();
    expect(screen.getByText("当前状态")).toBeInTheDocument();
    expect(screen.getByText("虚拟扩展槽")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "槽位选择" })).toBeInTheDocument();
    expect(screen.getByText("已扫描 20 个因子")).toBeInTheDocument();
  });

  it("releases the connection when disconnecting", async () => {
    render(<App />);
    const user = await connectToGame();
    await user.click(screen.getByRole("button", { name: "断开" }));
    await waitFor(() => expect(screen.getByRole("heading", { name: "连接正在运行的游戏" })).toBeInTheDocument());
  });

  it("switches the complete interface to English", async () => {
    render(<App />);
    const user = await connectToGame();
    await user.click(screen.getByRole("button", { name: "English" }));
    await waitFor(() => expect(screen.getByText("CURRENT CHARACTER")).toBeInTheDocument());
    expect(screen.getByRole("heading", { name: "Slot selection" })).toBeInTheDocument();
    expect(screen.getByText("VIRTUAL EXTENSION SLOTS")).toBeInTheDocument();
  });

  it("filters inventory items in the picker", async () => {
    render(<App />);
    const user = await connectToGame();
    await user.click(screen.getAllByRole("button", { name: "选择因子" })[0]);
    expect(screen.getByRole("heading", { name: "选择库存因子 · 槽位 19" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "扩展占用" }));
    expect(screen.getByText("Supplemental Damage III")).toBeInTheDocument();
    expect(screen.getByText("Nimble Onslaught III")).toBeInTheDocument();
    expect(screen.queryByText("Critical Hit V")).not.toBeInTheDocument();
  });

  it("requires confirmation before reducing active slots", async () => {
    render(<App />);
    const user = await connectToGame();
    const countInput = screen.getByRole("spinbutton", { name: "扩展槽数量" });
    await user.clear(countInput);
    await user.type(countInput, "4");
    await user.click(screen.getByRole("button", { name: "保存并重启生效" }));
    expect(screen.getByRole("heading", { name: "确认缩减扩展因子槽" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "确认" }));
    await waitFor(() => expect(screen.getByText("待生效 4")).toBeInTheDocument());
  });

  it("opens the preset manager with character and preset lists", async () => {
    render(<App />);
    const user = await connectToGame();
    await user.click(screen.getByRole("button", { name: "管理" }));
    expect(screen.getByRole("heading", { name: "管理预设" })).toBeInTheDocument();
    expect(screen.getByText("角色")).toBeInTheDocument();
    expect(screen.getByText("预设")).toBeInTheDocument();
    expect(within(screen.getByRole("dialog", { name: "管理预设" })).getByRole("button", { name: "套用" })).toBeInTheDocument();
  });
});
