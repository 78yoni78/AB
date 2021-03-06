﻿module Asmb.AST.Parsing

open FParsec
open Asmb


type 'a Parser = Parser<'a,unit>

//let spacesOrNewline = spaces
//let spaces = skipMany (pchar ' ' <|> pchar '\t')

let psize: _ Parser = 
    choice [
        stringReturn "byte" Byte
        stringReturn "word" Word
        stringReturn "dword" DWord
    ]
    .>> spaces

let pliteral: Literal Parser = 
    choice [
        puint32 |>> UInt 
        pchar '\'' >>. anyChar .>> pchar '\'' |>> Char
    ]
    .>> spaces

let pidentifier : string Parser =
    let isIdentifierFirstChar c = isLetter c || c = '_'
    let isIdentifierChar c = isLetter c || isDigit c || c = '_'

    many1Satisfy2L isIdentifierFirstChar isIdentifierChar "identifier" .>> spaces

let pexpr, piexpr = createParserForwardedToRef () 
//let pidOrIndex = 
//    (pidentifier |>> Variable)
//    <|> (pchar '[' >>. opt psize .>> spaces .>>. pexpr .>> spaces .>> pchar ']' |>> fun (s,e) -> Index (e,Option.defaultValue Byte s))

let sepBy1Save (p:'a Parser) (sep:'b Parser) : _ Parser = p .>>. many (sep .>>. p) 
    
/// Also parser whitespace before and after
let patom = 
    choice [
        pliteral |>> Constent
        //pstring "offset " >>. pidentifier |>> Offset 
        pchar '(' >>. pexpr .>> pchar ')'
        pidentifier |>> Variable
        parse {
            let! name = pidentifier
            let! param = 
                pchar '(' >>. sepBy pexpr (pchar ',') .>> pchar ')'
                |> opt
            return match param with None -> Variable name | Some param -> Call(name, param)
        }
    ]
    .>> spaces 

let pterm = 
    parse {
        let! operators = many (choice [
            stringReturn "&" PointerOf
            stringReturn "*" PointerVal
            stringReturn "!" Not
        ] .>> spaces) 
        let! atom = patom
        return List.foldBack(fun o e -> UOperation(o, e)) operators atom
    }

do piexpr :=
    let binary sep p = 
        sepBy1Save p (sep .>> spaces)
        |>> fun (fst, rest) -> List.fold (fun e1 (o, e2) -> BiOperation(o,e1,e2)) fst rest
    pterm
    |> binary (choice [stringReturn "*" Mul; stringReturn "/" Div; stringReturn "%" Mod])
    |> binary (choice [stringReturn "+" Add; stringReturn "-" Sub])
    |> binary (choice [stringReturn "=" EQ; stringReturn "!=" NEQ; stringReturn "<" Lesser; stringReturn ">" Greater; stringReturn ">=" NLesser; stringReturn "<=" NGreater])


let pblock, piblock = createParserForwardedToRef()

let pcomment = 
    choice [
        pstring "//" >>. restOfLine true
        pstring "/*" >>. manyCharsTill anyChar (pstring "*/")
    ] |>> Comment

let pstatement = 
    choice [
        pcomment |>> Statement.Comment
        pstring "return" >>. spaces >>. opt pexpr |>> Return    
        pstring "###" >>. spaces >>. manyCharsTill anyChar (pstring "###") |>> fun x -> x.Split('\r') |> NativeAssemblyLines
        pstring "pushpop" >>. spaces >>. sepBy1 pterm (pchar ',' >>. spaces) .>>. pblock |>> Pushpop
        pstring "if" >>. spaces >>. tuple3 pexpr pblock (pstring "else" >>. spaces >>. pblock) |>> IfElse
        pstring "while" >>. spaces >>. pexpr .>>. pblock |>> While
        //pidentifier .>>.? (stringReturn "++" Increment <|> stringReturn "--" Decrement) |>> fun (a,f) -> f a
        pterm .>> pstring "<-" .>> spaces .>>. pexpr |>> Assign
    ]
    .>> spaces

do piblock :=
    pchar '{' >>. spaces >>. pcomment .>>. many pstatement .>> pchar '}' |>> Block


let pproc = 
    let parameter = pidentifier .>>. psize
    pstring "func" >>. psize >>. spaces >>. pidentifier 
    .>> pchar '(' .>> spaces .>>. sepBy parameter (pchar ',' >>. spaces) .>> pchar ')' .>> spaces
    .>>. pblock
    |>> fun ((id,param),s) -> {ProcName = id; ProcBody = s; Parameters = param}
        
let pprogram = 
    let parray: _ Parser = 
        choice [
            pchar '{' >>. spaces >>. sepEndBy1 pliteral (pchar ',' .>> spaces) .>> pchar '}'
            pint32 .>>? spaces .>>? pstring "dup" .>> spaces .>>. pliteral |>> fun (a,b) -> List.replicate a b
            pliteral |>> List.singleton
        ]
    let variableDeclaration = 
        tuple3 pidentifier psize <| opt (pstring "<-" >>. spaces >>. parray)

    spaces >>. many variableDeclaration .>>. many1 pproc .>> eof
    |>> fun (vars,procs) -> {ProgProcedures = procs; ProgVariables = List.map (fun (id,size,v) -> id, size, Option.defaultValue [UInt 0u] v) vars}
