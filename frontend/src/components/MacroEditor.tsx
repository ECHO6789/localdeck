import type { MacroDefinition, MacroStep } from '../types'

type Props = {
  macros: MacroDefinition[]
  bindings: Record<string, string>
  disabled?: boolean
  onChange: (macros: MacroDefinition[]) => void
  onBindingsChange: (bindings: Record<string, string>) => void
}

const stepTypes = [
  { value: 'key', label: '按键组合' },
  { value: 'text', label: '输入文本' },
  { value: 'mouse', label: '鼠标点击' },
  { value: 'mouseAt', label: '坐标点击' },
  { value: 'run', label: '打开程序/网址' },
  { value: 'delay', label: '等待毫秒' },
]

export function MacroEditor({
  macros,
  bindings,
  disabled = false,
  onChange,
  onBindingsChange,
}: Props) {
  const updateMacro = (id: string, patch: Partial<MacroDefinition>) => {
    onChange(macros.map((macro) => (macro.id === id ? { ...macro, ...patch } : macro)))
  }

  const updateStep = (macroId: string, index: number, patch: Partial<MacroStep>) => {
    const macro = macros.find((item) => item.id === macroId)
    if (!macro) return
    const steps = macro.steps.map((step, stepIndex) =>
      stepIndex === index ? { ...step, ...patch } : step,
    )
    updateMacro(macroId, { steps })
  }

  const addStep = (macroId: string, type: string) => {
    const macro = macros.find((item) => item.id === macroId)
    if (!macro) return
    const step: MacroStep = {
      type,
      value: type === 'key' ? 'Ctrl+C' : type === 'mouse' ? 'left' : type === 'delay' ? '100' : '',
      delayMs: 0,
      x: 0,
      y: 0,
    }
    updateMacro(macroId, { steps: [...macro.steps, step] })
  }

  const removeStep = (macroId: string, index: number) => {
    const macro = macros.find((item) => item.id === macroId)
    if (!macro) return
    updateMacro(macroId, { steps: macro.steps.filter((_, stepIndex) => stepIndex !== index) })
  }

  const addMacro = () => {
    const id = crypto.randomUUID().replaceAll('-', '')
    onChange([
      ...macros,
      {
        id,
        name: `宏 ${macros.length + 1}`,
        enabled: true,
        steps: [{ type: 'key', value: 'Ctrl+C', delayMs: 0, x: 0, y: 0 }],
      },
    ])
  }

  const removeMacro = (id: string) => {
    onChange(macros.filter((macro) => macro.id !== id))
  }

  return (
    <div className="macro-editor">
      <div className="macro-bindings">
        {['MouseX1', 'MouseX2'].map((button) => (
          <label key={button} className="macro-binding-row">
            <span>{button === 'MouseX1' ? '鼠标侧键 1（Back）' : '鼠标侧键 2（Forward）'}</span>
            <select
              value={bindings[button] ?? ''}
              disabled={disabled}
              onChange={(event) =>
                onBindingsChange({ ...bindings, [button]: event.target.value })
              }
            >
              <option value="">不绑定</option>
              {macros.map((macro) => (
                <option key={macro.id} value={macro.id}>
                  {macro.name}
                </option>
              ))}
            </select>
          </label>
        ))}
      </div>

      <div className="macro-list">
        {macros.map((macro) => (
          <article className="macro-card" key={macro.id}>
            <div className="macro-card-head">
              <input
                className="macro-name"
                value={macro.name}
                disabled={disabled}
                onChange={(event) => updateMacro(macro.id, { name: event.target.value })}
              />
              <button
                className={`toggle ${macro.enabled ? 'checked' : ''}`}
                disabled={disabled}
                aria-pressed={macro.enabled}
                onClick={() => updateMacro(macro.id, { enabled: !macro.enabled })}
              >
                <span />
              </button>
              <button className="icon-button" disabled={disabled} onClick={() => removeMacro(macro.id)} title="删除宏">
                ×
              </button>
            </div>

            <div className="macro-steps">
              {macro.steps.map((step, index) => (
                <div className="macro-step" key={`${macro.id}-${index}`}>
                  <select
                    value={step.type}
                    disabled={disabled}
                    onChange={(event) => updateStep(macro.id, index, { type: event.target.value })}
                  >
                    {stepTypes.map((type) => (
                      <option key={type.value} value={type.value}>{type.label}</option>
                    ))}
                  </select>
                  <input
                    value={step.value}
                    disabled={disabled || step.type === 'delay'}
                    placeholder={placeholderFor(step.type)}
                    onChange={(event) => updateStep(macro.id, index, { value: event.target.value })}
                  />
                  <label className="delay-field">
                    <span>等待</span>
                    <input
                      type="number"
                      min={0}
                      max={10000}
                      value={step.delayMs}
                      disabled={disabled}
                      onChange={(event) =>
                        updateStep(macro.id, index, { delayMs: Number(event.target.value) })
                      }
                    />
                    <span>ms</span>
                  </label>
                  {step.type === 'mouseAt' && (
                    <span className="coord-field">{step.x ?? 0},{step.y ?? 0}</span>
                  )}
                  <button className="step-remove" disabled={disabled} onClick={() => removeStep(macro.id, index)}>
                    ×
                  </button>
                </div>
              ))}
            </div>

            <div className="macro-add-row">
              {stepTypes.map((type) => (
                <button
                  key={type.value}
                  className="text-button"
                  disabled={disabled}
                  onClick={() => addStep(macro.id, type.value)}
                >
                  + {type.label}
                </button>
              ))}
            </div>
          </article>
        ))}
      </div>
      <button className="button primary" disabled={disabled} onClick={addMacro}>+ 新建宏</button>
    </div>
  )
}

function placeholderFor(type: string): string {
  return {
    key: '例如 Ctrl+Shift+T',
    text: '要输入的文本',
    mouse: 'left / right / middle',
    mouseAt: 'left / right / middle',
    run: 'C:\\path\\app.exe 或 https://…',
    delay: '',
  }[type] ?? ''
}

export default MacroEditor
