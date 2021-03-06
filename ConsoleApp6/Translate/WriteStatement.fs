﻿module Asmb.Translate.WriteStatement

open Asmb
open Asmb.AST
open Asmb.IL

open LineWriter
open WriteExpr


let rec writeStatement (statement: Statement): LineWriter -> LineWriter = 
    match statement with
    | Pushpop ([], block) -> 
        writeBlock block
    | Pushpop (o::l, block) ->
        append1 (Line.comment <| sprintf "Pushing %O" o)
        >> writeExpr o
        >> append1 IndentIn
        >> writeStatement (Pushpop (l, block))
        >> append1 IndentOut
        >> append1 (Line.comment <| sprintf "Popping %O" o)
        >> writeAssignTo o
    | SideEffect e ->
        exprSize e (fun size -> 
            writeExpr e >> append1 (Line.make "add" [Reg SP; Size.bytes size |> UInt |> Constent]))
    | Assign (assign, expr) ->
        exprSize assign (fun size ->
            writeExpr (Convert(expr, size)) >> writeAssignTo assign)
    | StackDeclare (name, size, None) ->
        let r = Register.fromSize A size
        //  Simply put some zeros to mark it on the stack
        declare (name, size) 
        >> append1 (Line.mov0 r)
        >> append (Line.push r)
    | StackDeclare (name, size, Some expr) ->
        declare (name, size) >> writeExpr (Convert (expr, size))
    | Statement.Comment c -> id //[Line.Comment c]
    | IfElse (cond, trueBlock, falseBlock) ->
        let appendIfBody skipElse =
            makeLabel (SkipIf, cond, falseBlock |> Option.bind (fun b -> b.Comment)) (fun skipIf ->
                writeCondition skipIf cond
                >> append1 IndentIn
                >> writeBlock trueBlock
                >> match skipElse with Some skipElse -> append1 (Jump (JMP, skipElse)) | None -> id
                >> append1 IndentOut
                >> append1 (Line.Label skipIf))

        match falseBlock with
        | None -> 
            appendIfBody None
        | Some falseBlock -> 
            makeLabel(SkipElse, cond, falseBlock.Comment) (fun skipElse ->
                appendIfBody (Some skipElse)
                >> writeBlock falseBlock
                >> append1 (Line.Label skipElse)) 
    | While (cond, block) ->
        func {
            let! skip = makeLabel (EndLabel, cond, None)
            do! func {
                let! loop = makeLabel (LoopLabel, cond, block.Comment)
                do! append1 (Line.Label loop)
                do! writeCondition skip cond
                do! func {
                    do! indented
                    do! writeBlock block
                    do! append1 (Jump (JMP, loop))
                }
                do! append1 (Line.Label skip)
            }
        }
    | Return e ->
        func {
            //  Store result
            match e with
            | None -> ()
            | Some e ->
                let! size = exprSize e
                let resReg = Register.fromSize A size
                do! writeExpr e
                do! append (Line.pop resReg)

            //  Ret
            do! procedureStack (fun stack -> append1 (Line.make "add" [Reg SP; Constent (UInt stack)]))
            do! paramStack (fun stack -> append1 (Line.make "ret" [Constent (UInt stack)]))
        }
    //| Return None -> //translateStatement (Return (Some (Expr.Constent <| UInt 0u)))
    //    procedureStack (fun stack -> append1 (Line.make "add" [Reg SP; Constent (UInt stack)]))
    //    >> paramStack (fun stack -> append1 (Line.make "ret" [Constent (UInt stack)]))
    //| Return (Some expr) ->
    //    exprSize expr (Register.fromSize A >> fun a -> 
    //        let d = Register.fromSize D Word
    //        writeExpr expr
    //        >> append (Line.pop a)
    //        >> procedureStack (fun stack -> append1 (Line.make "add" [Reg SP; Constent (UInt stack)]))
    //        >> append1 (Line.make "pop" [Reg d])
    //        >> paramStack (fun stack -> append1 (Line.make "add" [Reg SP; Constent (UInt stack)]))
    //        >> append (Line.push a)
    //        >> append (Line.push d)
    //        >> append1 (Line.make "ret" []))
    | UnsafePush e -> writeExpr e
    | UnsafePop e -> writeAssignTo e
    | NativeAssemblyLines lines -> Seq.map Line.Text lines |> List.ofSeq |> append
 

and writeBlock (Block (_, statements)) writer: LineWriter = 
    List.fold (fun writer statement -> 
        writer 
        |> append1 EmptyLine 
        |> match statement with 
            | NativeAssemblyLines _ -> id 
            | _ -> append1 (Line.comment <| string statement) 
        |> writeStatement statement) writer statements
